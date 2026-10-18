using System.Globalization;
using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

// Le point d'étape du test mémoire : un plantage pendant le test en est le résultat, jamais un succès perdu.
public sealed class RepriseDuTestMemoireTests
{
    private static readonly DateTimeOffset Debut = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);
    private static readonly RepriseDuTestMemoire.Point Point = new(Debut, Debut.AddMinutes(3), 4);

    [Fact]
    public void Un_arret_brutal_pendant_le_test_dit_que_la_machine_s_est_arretee_pendant_le_test()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = RepriseDuTestMemoire.Juger(Point, [Debut.AddMinutes(4)], [], [Debut.AddMinutes(4).AddSeconds(-20)])!;
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("arrêt brutal").And.Contain("l'échoue");
    }

    // Kernel-Power 41 est noté au démarrage qui suit la panne, pas à l'heure de la panne : un PC planté pendant le test
    // et rallumé trois heures plus tard a échoué le test.
    [Fact]
    public void Un_arret_note_au_premier_demarrage_apres_le_test_compte_meme_rallume_bien_plus_tard()
    {
        var rallume = Debut.AddHours(3);
        RepriseDuTestMemoire.Juger(Point, [rallume.AddSeconds(30)], [], [rallume])!.Statut.Should().Be(RepairStatus.Suspicious);
    }

    // Un redémarrage propre après le test, puis une panne le lendemain : la panne n'est pas celle du test.
    [Fact]
    public void Un_arret_note_a_un_demarrage_plus_tardif_n_est_pas_rattache_au_test()
    {
        var propre = Debut.AddHours(1);
        var lendemain = Debut.AddDays(1);
        RepriseDuTestMemoire.Juger(Point, [lendemain.AddSeconds(30)], [], [propre, lendemain])!.Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Une_erreur_materielle_grave_pendant_le_test_compte_aussi()
        => RepriseDuTestMemoire.Juger(Point, [], [Debut.AddMinutes(1)], [])!.Statut.Should().Be(RepairStatus.Suspicious);

    // Notre règle : l'appui long sur le bouton n'est pas un échec de la mémoire. L'appelant ne passe que les arrêts hors
    // bouton ; sans arrêt ni erreur, un test sans fin est « pas fini », jamais réussi.
    [Fact]
    public void Sans_arret_ni_erreur_le_test_est_pas_fini_et_jamais_reussi()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = RepriseDuTestMemoire.Juger(Point, [], [], [])!;
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("pas fini");
        c.Explication.Should().Contain("Pas fini n'est pas réussi");
    }

    [Fact]
    public void Un_arret_bien_apres_le_test_ou_avant_lui_ne_lui_est_pas_rattache()
        => RepriseDuTestMemoire.Juger(Point, [Debut.AddHours(5), Debut.AddMinutes(-5)], [], [Debut.AddHours(1), Debut.AddHours(5).AddSeconds(-20)])!
            .Statut.Should().Be(RepairStatus.Observed);

    [Fact]
    public void Journaux_non_lus_est_dit_non_lu_et_sans_point_rien()
    {
        RepriseDuTestMemoire.Juger(Point, null, [], []).Should().Match<RepairFinding>(c => c.Statut == RepairStatus.Unknown);
        RepriseDuTestMemoire.Juger(Point, [], [], null).Should().Match<RepairFinding>(c => c.Statut == RepairStatus.Unknown);
        RepriseDuTestMemoire.Juger(null, [], [], []).Should().BeNull();
    }

    [Fact]
    public void Le_point_se_pose_se_relit_et_s_efface()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"reprise-{Guid.NewGuid():N}.json");
        RepriseDuTestMemoire.Poser(chemin, Point);
        RepriseDuTestMemoire.Lire(chemin).Should().Be(Point);
        RepriseDuTestMemoire.Effacer(chemin);
        RepriseDuTestMemoire.Lire(chemin).Should().BeNull();
    }

    // Le verdict se dit une fois : le point s'efface derrière lui, sauf quand les journaux n'ont pas été lus (il se relira).
    [Fact]
    public void Le_verdict_se_dit_une_fois_puis_le_point_s_efface()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"reprise-{Guid.NewGuid():N}.json");
        try
        {
            RepriseDuTestMemoire.Poser(chemin, Point);
            RepriseDuTestMemoire.JugerUneFois(chemin, Point, null, [], []).Should().Match<RepairFinding>(c => c.Statut == RepairStatus.Unknown);
            RepriseDuTestMemoire.Lire(chemin).Should().Be(Point, "journaux non lus : le point se relira");

            RepriseDuTestMemoire.JugerUneFois(chemin, Point, [Debut.AddMinutes(4)], [], [Debut.AddMinutes(4).AddSeconds(-20)])!
                .Statut.Should().Be(RepairStatus.Suspicious);
            RepriseDuTestMemoire.Lire(chemin).Should().BeNull("le verdict est rendu : il ne revient pas à chaque lancement");
        }
        finally { File.Delete(chemin); }
    }

    // Le test ne prend jamais plus de la moitié de la mémoire libre : il ne doit pas faire paginer la machine.
    [Theory]
    [InlineData(16L << 30, 2UL << 30, 1L << 30)]
    [InlineData(16L << 30, 30UL << 30, 4L << 30)]
    public void Le_volume_teste_est_borne_par_la_memoire_libre(long total, ulong libre, long attendu)
        => TestMemoireEnSession.Volume(total, libre).Should().Be(attendu);

    [Fact]
    public void Sans_lecture_de_la_memoire_libre_le_volume_reste_le_quart()
        => TestMemoireEnSession.Volume(16L << 30, null).Should().Be(4L << 30);

    [Fact]
    public void Chaque_passe_finie_est_annoncee_pour_le_point_d_etape()
    {
        var passes = new List<int>();
        TestMemoireEnSession.Lancer(1 << 16, TimeSpan.Zero, 1, CancellationToken.None, passes.Add);
        passes.Should().Equal(1);
    }
}
