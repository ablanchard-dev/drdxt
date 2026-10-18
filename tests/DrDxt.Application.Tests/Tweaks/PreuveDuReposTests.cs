using DrDxt.Tests.Shared;
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
    private static readonly TweakSnapshot Snapshot = new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"));

    private static ITweak Levier(bool ecrit = true)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(Snapshot.TweakId);
        t.ApplyAsync().Returns(ecrit ? Result<TweakSnapshot>.Ok(Snapshot) : Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "non"));
        t.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        return t;
    }

    // Une écriture journalisée posée dans une portée note son acquit, comme le client élevé de l'App ; l'acquit dit si
    // l'avant et l'écriture étaient déjà gardés quand il est parti.
    private static ITweak LevierQuiAcquitte(PreuvesEnAttente attente, List<string> log)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(Snapshot.TweakId);
        t.ApplyAsync().Returns(_ =>
        {
            log.Add("noté");
            PorteeDePose.Courante?.Noter(() =>
            {
                log.Add(attente.Charger() is not null ? "acquit, preuve gardée" : "acquit sans preuve");
                return Task.FromResult(Result.Ok());
            });
            return Task.FromResult(Result<TweakSnapshot>.Ok(Snapshot));
        });
        return t;
    }

    [Fact]
    public async Task L_ecriture_s_acquitte_une_fois_apres_que_la_preuve_est_gardee()
    {
        var attente = new PreuvesEnAttente(Fichier());
        var log = new List<string>();

        (await PreuveDuRepos.EcrireAsync(LevierQuiAcquitte(attente, log), new SondeEnFile(Serie(55)), 6, SansAttente, attente))
            .Should().BeTrue();

        log.Should().Equal("noté", "acquit, preuve gardée");
    }

    // La preuve n'a pas pu être gardée : seul le helper sait rendre l'origine, rien n'est acquitté.
    [Fact]
    public async Task Une_preuve_qui_ne_se_garde_pas_n_acquitte_rien()
    {
        var dossier = Directory.CreateDirectory(Fichier()).FullName;
        var attente = new PreuvesEnAttente(dossier);
        var log = new List<string>();

        var ecrire = () => PreuveDuRepos.EcrireAsync(LevierQuiAcquitte(attente, log), new SondeEnFile(Serie(55)), 6, SansAttente, attente);

        try
        {
            await ecrire.Should().ThrowAsync<Exception>("le fichier de la preuve est un dossier");
            log.Should().Equal("noté");
        }
        finally { Directory.Delete(dossier, recursive: true); }
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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lien, string cible, IntPtr securite);

    // Le fichier de la preuve vit dans le profil du joueur et le helper élevé le rejoue au retrait : un lien dur posé sous
    // le nom prévisible « <fichier>.tmp » faisait réécrire sa cible par l'App élevée. Les deux magasins passent par
    // l'écriture atomique, au temporaire aléatoire.
    [Fact]
    public void Un_lien_dur_pose_sous_le_nom_du_temporaire_ne_fait_jamais_reecrire_sa_cible()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-preuve-").FullName;
        try
        {
            var protege = Path.Combine(dossier, "protege.txt");
            File.WriteAllText(protege, "intact");
            var preuve = Path.Combine(dossier, "preuve.json");
            var retenu = Path.Combine(dossier, "retenu.json");
            CreateHardLink(preuve + ".tmp", protege, IntPtr.Zero).Should().BeTrue();
            CreateHardLink(retenu + ".tmp", protege, IntPtr.Zero).Should().BeTrue();

            new PreuvesEnAttente(preuve).Garder(new PreuveEnAttente(Snapshot.TweakId, Snapshot.OriginalValueJson, null, [10]));
            new ReglagesRetenus(retenu).Garder(new ReglageRetenu(Snapshot.TweakId, Snapshot.OriginalValueJson, null, DateTimeOffset.Now));

            File.ReadAllText(protege).Should().Be("intact");
            new PreuvesEnAttente(preuve).Charger().Should().NotBeNull();
            new ReglagesRetenus(retenu).Charger().Should().NotBeNull();
        }
        finally { Directory.Delete(dossier, recursive: true); }
    }
}
