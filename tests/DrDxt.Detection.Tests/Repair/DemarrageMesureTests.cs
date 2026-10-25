using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Une mesure de demarrage de Windows peut dater d'une autre annee : sans l'annee, ces chiffres passeraient pour
/// actuels. Au-dela de la fenetre, DrDXT le dit et ne cite rien.
/// </summary>
public class DemarrageMesureTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fenetre = TimeSpan.FromDays(90);

    private static MesureDemarrage Boot(DateTimeOffset quand, long ms) => new(quand, true, "demarrage", ms, 0);
    private static MesureDemarrage Appli(DateTimeOffset quand, string nom, long degradation) => new(quand, false, nom, degradation + 1000, degradation);

    [Fact]
    public void Une_mesure_trop_ancienne_est_dite_avec_son_annee_et_sans_chiffres()
    {
        var juillet2025 = new DateTimeOffset(2025, 7, 31, 15, 53, 0, TimeSpan.Zero);
        var constats = DemarrageMesure.Juger(
            [Boot(juillet2025, 59672), Appli(juillet2025, "Antimalware Service Executable", 39917)], Maintenant, Fenetre);

        constats.Should().ContainSingle();
        // Lue mais perimee : observee, pas « non lue » (le rapport reel la rangeait dans « Non lus »).
        constats[0].Statut.Should().Be(RepairStatus.Observed);
        constats[0].ValeurCourante.Should().StartWith("périmée");
        constats[0].Explication.Should().Contain(juillet2025.ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)).And.NotContain("39,9");
    }

    // Le dernier allumage ; sa date s'affiche à l'heure de la machine, l'attendu suit le même fuseau.
    private static readonly DateTimeOffset Dernier = new(2026, 9, 23, 19, 0, 38, TimeSpan.Zero);

    [Fact]
    public void Une_mesure_recente_nomme_les_applications_du_dernier_demarrage_par_retard()
    {
        var hier = Maintenant.AddDays(-1);
        var avant = Maintenant.AddDays(-20);
        var constats = DemarrageMesure.Juger(
        [
            Boot(hier, 30000), Appli(hier, "Discord", 3481), Appli(hier, "Steam", 10720),
            Boot(avant, 60000), Appli(avant, "Ancienne appli", 50000),
        ], Maintenant, Fenetre);

        constats.Select(c => c.Nom).Should().Equal("mesure de Windows", "Steam", "Discord");
        constats[1].ValeurCourante.Should().Be("+10,7 s");
    }

    [Fact]
    public void Sans_mesure_de_Windows_rien_n_est_affirme()
        => DemarrageMesure.Juger([], Maintenant, Fenetre).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Unknown);

    // --- Le temps du BIOS (Kernel-Boot 30), la mesure qui reste quand l'evenement 100 ne vient plus. ---

    [Fact]
    public void Le_temps_du_BIOS_est_celui_du_dernier_allumage_avec_la_plage_des_precedents()
    {
        // Les quatre allumages reels de la machine de reference, 19-23/09/2026.
        var constats = DemarrageMesure.JugerBios(
        [
            new(Dernier, 11386, 3685),
            new(new DateTimeOffset(2026, 9, 22, 16, 26, 4, TimeSpan.Zero), 11380, 3176),
            new(new DateTimeOffset(2026, 9, 20, 16, 43, 40, TimeSpan.Zero), 11208, 3445),
            new(new DateTimeOffset(2026, 9, 19, 17, 9, 18, TimeSpan.Zero), 11214, 3674),
        ]);

        var c = constats.Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.Observed, "aucune source ne dit a partir de quand c'est trop long");
        c.Domaine.Should().Be("BIOS", "range sur la page BIOS, a cote de Memory Fast Boot et MSI Fast Boot qui le gouvernent");
        c.ValeurCourante.Should().Be("11,4 s"); // dans la langue du docteur, francais par defaut dans les tests
        c.Explication.Should().Contain(Dernier.ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)).And.Contain("4 derniers").And.Contain("Dernier temps BIOS");
    }

    [Fact]
    public void Un_BIOS_qui_ne_publie_rien_est_dit_inconnu_pas_rapide()
    {
        DemarrageMesure.JugerBios([]).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Le_journal_Systeme_se_lit_sur_cette_machine()
    {
        var lu = new WindowsBootPerformanceLog().LireTempsBios();
        lu.IsOk.Should().BeTrue(lu.Error?.Message);
        // Un runner de CI n'a aucun demarrage journalise ; une liste vide est un etat valide,
        // et OnlyContain echoue sur le vide. On verifie qu'aucune valeur lue n'est hors plage.
        lu.Value!.Should().NotContain(a => a.BiosMs <= 0 || a.BiosMs >= 600_000, "des millisecondes d'allumage, pas des nanosecondes");
    }
}
