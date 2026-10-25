using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Un ventilateur qui frotte et s'arrête passe inaperçu jusqu'au bruit : chaque partie garde chaque ventilateur de la carte mère, et le docteur dit celui qui s'arrête ou ne tourne plus.
public sealed class VentilateursEnPartieTests
{
    private static IReadOnlyList<VentilateurLu> Seconde(params (int Index, int TrMin)[] v)
        => v.Select(x => new VentilateurLu(x.Index, x.TrMin, 50)).ToList();

    [Fact]
    public void Chaque_ventilateur_qui_tourne_est_resume_et_ses_secondes_a_l_arret_comptees()
    {
        var secondes = Enumerable.Repeat(Seconde((0, 1200), (5, 1100), (1, 0)), 50)
            .Concat(Enumerable.Repeat(Seconde((0, 1250), (5, 0), (1, 0)), 8))
            .Concat([null]);

        var r = VentilateursEnPartie.Depuis(secondes)!;

        r.Select(v => v.Index).Should().Equal([0, 5], "l'en-tête 1 n'a jamais tourné : rien de branché, rien à dire");
        r[1].SecondesArret.Should().Be(8);
        r[1].Secondes.Should().Be(58, "la seconde non lue ne compte pas");
        r[0].TrMinMin.Should().Be(1200);
        VentilateursEnPartie.Depuis([null, null]).Should().BeNull("rien lu : rien à résumer");
    }

    [Fact]
    public void Le_cumul_des_captures_additionne_les_arrets_et_pondere_la_moyenne()
    {
        var a = new[] { new VentilateurEnPartie(5, 1100, 1000, 0, 60) };
        var b = new[] { new VentilateurEnPartie(5, 500, 0, 30, 60) };
        var c = VentilateursEnPartie.Cumuler([a, null, b])!.Single();
        c.SecondesArret.Should().Be(30);
        c.Secondes.Should().Be(120);
        c.TrMinMoyen.Should().Be(800);
        c.TrMinMin.Should().Be(0);
    }

    private static VentilateursEnPartie.PartieVue Partie(int ilYAJours, params VentilateurEnPartie[] v)
        => new("cod.exe", new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero).AddDays(-ilYAJours), v);

    [Fact]
    public void Un_ventilateur_qui_s_arrete_pendant_la_partie_est_dit()
    {
        var c = VentilateursEnPartie.Juger([Partie(0, new(0, 1200, 1150, 0, 600), new(5, 900, 0, 42, 600))]).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("42 s").And.Contain("n° 5").And.Contain("frotte");
        VentilateursEnPartie.Juger([Partie(0, new VentilateurEnPartie(5, 900, 0, 3, 600))]).Should().BeEmpty("3 s : une mesure ratée, pas un arrêt");
        // Une seule lecture de la puce (toutes les 15 s) ratée compte 15 s : ce n'est pas un arrêt.
        VentilateursEnPartie.Juger([Partie(0, new VentilateurEnPartie(5, 900, 0, 15, 600))]).Should().BeEmpty("une lecture ratée, resservie 15 s");
        VentilateursEnPartie.Juger([Partie(0, new VentilateurEnPartie(5, 900, 0, 30, 600))]).Should().ContainSingle("deux lectures à l'arrêt");
    }

    [Fact]
    public void Un_ventilateur_qui_tournait_et_ne_tourne_plus_est_dit()
    {
        var avant = new VentilateurEnPartie(5, 1100, 1050, 0, 600);
        var cpu = new VentilateurEnPartie(0, 1200, 1150, 0, 600);
        var c = VentilateursEnPartie.Juger([Partie(0, cpu), Partie(1, cpu, avant), Partie(2, cpu, avant)]).Single();
        c.Explication.Should().Contain("ne tourne plus").And.Contain("n° 5");
        VentilateursEnPartie.Juger([Partie(0, cpu), Partie(1, cpu, avant)]).Should().BeEmpty("une seule partie d'avant : pas assez pour dire qu'il tournait");
        VentilateursEnPartie.Juger([Partie(0), Partie(1, cpu, avant), Partie(2, cpu, avant)]).Should().BeEmpty("rien lu sur la dernière partie : rien n'est conclu");
    }

    // Un ventilateur lu par HWiNFO porte son nom ; une partie lue par HWiNFO ne se compare jamais à une partie lue
    // par la puce de la carte (sinon un changement de source ferait dire « ne tourne plus » à tous les ventilateurs).
    [Fact]
    public void Le_nom_HWiNFO_est_dit_et_deux_sources_ne_se_comparent_pas()
    {
        VentilateursEnPartie.Juger([Partie(0, new VentilateurEnPartie(101, 900, 0, 30, 600, "Chassis1"))]).Single().Explication
            .Should().Contain("« Chassis1 »");
        var msi = new VentilateurEnPartie(5, 1100, 1050, 0, 600);
        var hw = new VentilateurEnPartie(100, 1200, 1150, 0, 600, "CPU");
        VentilateursEnPartie.Juger([Partie(0, hw), Partie(1, msi), Partie(2, msi)]).Should().BeEmpty("source changée : rien n'est conclu");
    }

    // Des parties relevées sans aucun ventilateur lu ⇒ « non lu », jamais un silence qui passerait pour
    // « tout va bien », avec le geste de secours (HWiNFO et sa mémoire partagée).
    [Fact]
    public void Aucun_ventilateur_lu_sur_les_parties_se_dit_non_lu_avec_le_secours()
    {
        var c = VentilateursEnPartie.Juger([Partie(0), Partie(1)]).Single();
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("HWiNFO");
        VentilateursEnPartie.Juger([]).Should().BeEmpty("aucune partie relevée : rien à dire");
    }
}
