using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

// Un réglage de la carte ne reste que prouvé. Au repos, la preuve est
// la consommation lue avant puis après ; sans séparation nette (toutes les lectures d'après sous toutes celles
// d'avant), le réglage est retiré. Aucun seuil inventé : c'est la dispersion mesurée qui décide.
public sealed class PreuveDuReposTests
{
    private sealed class SondeEnFile(params double?[] watts) : IHardwareProbe
    {
        private int _i;
        public HardwareReading Lire() => new(watts[Math.Min(_i++, watts.Length - 1)], null);
    }

    private static readonly Func<TimeSpan, Task> SansAttente = _ => Task.CompletedTask;
    private static readonly TweakSnapshot Snapshot = new("gpu.repos_global_normal", "inverse");

    private static ITweak Levier(bool ecrit = true)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(Snapshot.TweakId);
        t.ApplyAsync().Returns(ecrit ? Result<TweakSnapshot>.Ok(Snapshot) : Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "non"));
        t.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        return t;
    }

    private static double?[] Serie(double baseW, int n = 6) => Enumerable.Range(0, n).Select(i => (double?)(baseW + (i % 3) * 0.5)).ToArray();

    [Fact]
    public async Task Une_baisse_nette_est_prouvee_et_le_reglage_reste()
    {
        var levier = Levier();
        var sonde = new SondeEnFile([.. Serie(55), .. Serie(14)]);

        var v = await PreuveDuRepos.ProuverAsync(levier, sonde, 6, SansAttente);

        v.Etat.Should().Be(EtatDeLaPreuve.Prouve);
        v.AvantW.Should().BeApproximately(55.5, 0.01);
        v.ApresW.Should().BeApproximately(14.5, 0.01);
        await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Des_series_qui_se_chevauchent_ne_prouvent_rien_et_le_reglage_est_retire()
    {
        var levier = Levier();
        var sonde = new SondeEnFile([.. Serie(20), .. Serie(19.5)]);

        var v = await PreuveDuRepos.ProuverAsync(levier, sonde, 6, SansAttente);

        v.Etat.Should().Be(EtatDeLaPreuve.PasDeGain);
        await levier.Received(1).RevertAsync(Snapshot);
    }

    [Fact]
    public async Task Des_watts_illisibles_ne_sont_pas_une_preuve_et_le_reglage_est_retire()
    {
        var levier = Levier();
        var sonde /* lisible avant, illisible après */ = new SondeEnFile([.. Serie(55), null]);

        var v = await PreuveDuRepos.ProuverAsync(levier, sonde, 6, SansAttente);

        v.Etat.Should().Be(EtatDeLaPreuve.NonMesure);
        await levier.Received(1).RevertAsync(Snapshot);
    }

    [Fact]
    public async Task Sans_watts_avant_rien_n_est_ecrit()
    {
        var levier = Levier();

        var v = await PreuveDuRepos.ProuverAsync(levier, new SondeEnFile((double?)null), 6, SansAttente);

        v.Etat.Should().Be(EtatDeLaPreuve.NonMesure);
        await levier.DidNotReceive().ApplyAsync();
    }

    // Mesure réelle : le global écrit ne change la carte qu'au redémarrage (chaque programme lit le profil
    // à son lancement). La preuve se fait en deux temps : l'avant et l'écriture sont gardés, le lancement suivant conclut.
    private static string Fichier() => Path.Combine(Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task En_deux_temps_la_preuve_attend_puis_garde_le_reglage_qui_baisse()
    {
        var levier = Levier();
        var attente = new PreuvesEnAttente(Fichier());

        (await PreuveDuRepos.EcrireAsync(levier, new SondeEnFile(Serie(55)), 6, SansAttente, attente)).Should().BeTrue();
        attente.Charger().Should().NotBeNull("le lancement suivant doit trouver l'avant et l'écriture");

        var v = await PreuveDuRepos.ConclureAsync(levier, new SondeEnFile(Serie(14)), 6, SansAttente, attente);

        v!.Etat.Should().Be(EtatDeLaPreuve.Prouve);
        v.BaisseW.Should().BeApproximately(41, 0.01);
        attente.Charger().Should().BeNull("une preuve conclue ne se rejoue pas");
        await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task En_deux_temps_sans_baisse_nette_le_reglage_est_retire()
    {
        var levier = Levier();
        var attente = new PreuvesEnAttente(Fichier());
        await PreuveDuRepos.EcrireAsync(levier, new SondeEnFile(Serie(55)), 6, SansAttente, attente);

        var v = await PreuveDuRepos.ConclureAsync(levier, new SondeEnFile(Serie(55)), 6, SansAttente, attente);

        v!.Etat.Should().Be(EtatDeLaPreuve.PasDeGain);
        await levier.Received(1).RevertAsync(Snapshot);
        attente.Charger().Should().BeNull();
    }

    [Fact]
    public async Task Sans_preuve_en_attente_le_lancement_ne_touche_a_rien()
    {
        var levier = Levier();

        (await PreuveDuRepos.ConclureAsync(levier, new SondeEnFile(Serie(14)), 6, SansAttente, new PreuvesEnAttente(Fichier())))
            .Should().BeNull();
        await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Sans_watts_avant_rien_n_est_ecrit_ni_mis_en_attente()
    {
        var levier = Levier();
        var attente = new PreuvesEnAttente(Fichier());

        (await PreuveDuRepos.EcrireAsync(levier, new SondeEnFile((double?)null), 6, SansAttente, attente)).Should().BeFalse();
        await levier.DidNotReceive().ApplyAsync();
        attente.Charger().Should().BeNull();
    }

    [Fact]
    public async Task Un_levier_qui_n_ecrit_pas_n_est_ni_mesure_ni_annule()
    {
        var levier = Levier(ecrit: false);

        var v = await PreuveDuRepos.ProuverAsync(levier, new SondeEnFile([.. Serie(55), .. Serie(14)]), 6, SansAttente);

        v.Etat.Should().Be(EtatDeLaPreuve.NonMesure);
        await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }
}
