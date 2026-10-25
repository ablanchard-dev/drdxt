using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Un incident est rapproché de ce qui a été installé dans les trois jours avant, dates à l'appui,
// sans jamais affirmer la cause.
public sealed class AvantLIncidentTests
{
    // Le journal d'installation est écrit à l'heure LOCALE et l'écran affiche l'heure locale. Figer +2 h (Paris)
    // faisait échouer ces tests sur la CI GitHub, en UTC (« 03:09 » au lieu de « 05:09 ») : la date se construit dans le
    // fuseau de la machine qui lance les tests.
    private static DateTimeOffset T(int mois, int jour, int h, int m)
    {
        var local = new DateTime(2026, mois, jour, h, m, 0, DateTimeKind.Unspecified);
        return new(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    // Extrait du journal relevé sur la machine le 03/10/2026 (setupapi.dev.20261003_193128.log), raccourci.
    private static readonly string[] Journal =
    [
        ">>>  [Device Install (DiInstallDevice) - PCI\\VEN_10DE&DEV_2882&SUBSYS_51611462&REV_A1\\4&22A14BB8&0&0008]",
        ">>>  Section start 2026/09/01 01:27:06.840",
        "     sto: {Setup Import Driver Package: c:\\programdata\\nvidia corporation\\display.driver\\nvmdi.inf} 01:27:06.897",
        "     inf:      Provider: NVIDIA",
        "     inf:      Class GUID: {4D36E968-E325-11CE-BFC1-08002BE10318}",
        "     inf:      Driver Version: 08/20/2026,32.0.16.1656",
        "<<<  Section end 2026/09/01 01:27:40.100",
        ">>>  Section start 2026/09/01 01:28:00.000",
        "     inf:      Provider: Microsoft",
        "     inf:      Driver Version: 06/21/2006,10.0.26100.1",
        ">>>  Section start 2026/10/03 19:30:44.840",
        "     inf:      Provider: NVIDIA",
        "     inf:      Driver Version: 09/17/2026,32.0.16.1714",
        ">>>  Section start 2026/10/04 10:00:00.000",
        "     inf:      Provider: NVIDIA",
        "     inf:      Driver Version: 09/17/2026,32.0.16.1714",
    ];

    [Fact]
    public void Le_journal_d_installation_rend_chaque_pilote_tiers_a_sa_premiere_apparition()
    {
        var p = AvantLIncident.PilotesInstalles(Journal);

        p.Should().HaveCount(2, "le pilote Microsoft est laissé, et la 1714 réimportée le 04/10 garde sa première date");
        p.Should().Contain(new FaitDate(T(9, 1, 1, 27).AddSeconds(6), "pilote NVIDIA 32.0.16.1656 (616.56)"));
        p.Should().Contain(new FaitDate(T(10, 3, 19, 30).AddSeconds(44), "pilote NVIDIA 32.0.16.1714 (617.14)"));
    }

    [Fact]
    public void Un_arret_brutal_le_lendemain_d_un_pilote_est_rapproche_de_lui_avec_l_ecart()
    {
        var pilotes = AvantLIncident.PilotesInstalles(Journal);
        var incidents = new[] { new FaitDate(T(9, 2, 5, 9), "arrêt brutal") };

        var c = AvantLIncident.Juger(incidents, pilotes, T(9, 10, 12, 0)).Single();

        c.Statut.Should().Be(RepairStatus.NonDefault, "l'incident a moins de trente jours : à regarder, un rapprochement de dates n'est pas une preuve");
        c.ValeurCourante.Should().Be("arrêt brutal le 02/09 à 05:09, juste avant : pilote NVIDIA 32.0.16.1656 (616.56), installé le 01/09 à 01:27, 28 h avant");
        c.Explication.Should().StartWith("Arrêt brutal le 02/09/2026 à 05:09. Dans les 3 jours avant : ")
            .And.Contain("pilote NVIDIA 32.0.16.1656 (616.56), installé le 01/09 à 01:27, 28 h avant")
            .And.NotContain("1714", "installé après l'incident")
            .And.Contain("pas une preuve");
    }

    [Fact]
    public void Au_dela_de_trois_jours_ou_apres_l_incident_rien_n_est_rapproche()
    {
        var pilotes = AvantLIncident.PilotesInstalles(Journal);
        var incidents = new[]
        {
            new FaitDate(T(9, 4, 16, 44), "arrêt brutal"),   // 3 jours et 15 h après la 1656
            new FaitDate(T(10, 3, 19, 0), "arrêt brutal"),   // 30 minutes AVANT la 1714
        };

        AvantLIncident.Juger(incidents, pilotes, T(10, 3, 20, 0)).Should().BeEmpty();
    }

    [Fact]
    public void Un_logiciel_installe_le_meme_jour_est_dit_sans_heure_et_peut_etre_apres()
    {
        var logiciel = new FaitDate(T(9, 2, 0, 0), "logiciel Truc 1.0", AuJour: true);
        var veille = new FaitDate(T(9, 1, 0, 0), "logiciel Machin 2.0", AuJour: true);
        var lendemain = new FaitDate(T(9, 3, 0, 0), "logiciel Après 3.0", AuJour: true);
        var ancien = new FaitDate(T(8, 28, 0, 0), "logiciel Ancien 4.0", AuJour: true);

        var pilote = new FaitDate(T(9, 1, 22, 0), "pilote NVIDIA 32.0.16.1656 (616.56)");
        var c = AvantLIncident.Juger([new FaitDate(T(9, 2, 5, 9), "écran bleu 0x00000133")], [logiciel, veille, lendemain, ancien, pilote], T(12, 1, 0, 0)).Single();

        c.Statut.Should().Be(RepairStatus.Observed, "plus de trente jours : un fait d'historique, pas une alerte");
        c.ValeurCourante.Should().Contain("pilote NVIDIA", "seul un pilote, daté à la seconde, est « juste avant »");
        c.Explication.Should().Contain("Logiciels installés ou mis à jour dans la même période")
            .And.Contain("logiciel Machin 2.0, installé ou mis à jour le 01/09")
            .And.Contain("logiciel Truc 1.0, installé ou mis à jour le même jour (heure inconnue, peut-être après)")
            .And.NotContain("Après 3.0").And.NotContain("Ancien 4.0", "cinq jours avant : hors de la fenêtre");
    }

    [Fact]
    public void Un_logiciel_date_au_jour_n_est_rapproche_que_si_tout_le_jour_tient_dans_la_fenetre()
    {
        // Relevé réel : PhysX installé le 01/09 (jour seul), écran bleu le 04/09 à 16:44. Depuis minuit, 3 jours et 16 h.
        var physx = new FaitDate(T(9, 1, 0, 0), "logiciel PhysX", AuJour: true);
        var ecran = new FaitDate(T(9, 4, 16, 44), "écran bleu 0x000000EF");

        AvantLIncident.Juger([ecran], [physx], T(9, 10, 0, 0)).Should().BeEmpty();
    }

    // Relevé sur la machine : Edge daté du 03/10, Chrome et Brave du 02/10, par leurs mises à jour automatiques.
    [Fact]
    public void Un_logiciel_seul_ne_rapproche_rien()
    {
        var edge = new FaitDate(T(10, 3, 0, 0), "logiciel Microsoft Edge", AuJour: true);
        AvantLIncident.Juger([new FaitDate(T(10, 3, 22, 0), "arrêt brutal sans code")], [edge], T(10, 4, 0, 0)).Should().BeEmpty();
    }

    [Fact]
    public void Un_incident_isole_des_mois_plus_tot_n_efface_pas_le_rapprochement()
    {
        var pilote = new FaitDate(T(9, 1, 1, 27), "pilote NVIDIA 32.0.16.1656 (616.56)");
        FaitDate[] incidents = [new(T(7, 2, 10, 0), "écran bleu 0x00000154"), new(T(9, 2, 5, 9), "écran bleu 0x00000154")];

        AvantLIncident.Juger(incidents, [pilote], T(9, 10, 0, 0)).Should().ContainSingle()
            .Which.ValeurCourante.Should().StartWith("écran bleu 0x00000154 le 02/09");
    }

    [Fact]
    public void Un_incident_deja_arrive_avant_l_installation_n_est_pas_rapproche_d_elle()
    {
        // Relevé réel : arrêts en mise en veille du 22/07 au 05/08, HidHide installé le 03/08.
        var hidhide = new FaitDate(T(8, 3, 2, 19), "pilote Nefarius 1.2.98.0");
        var veille = "arrêt brutal pendant une mise en veille";
        FaitDate[] incidents = [new(T(7, 22, 10, 0), veille), new(T(8, 4, 13, 3), veille), new(T(8, 5, 16, 15), veille),
            new(T(8, 4, 20, 0), "arrêt brutal sans code")];

        var c = AvantLIncident.Juger(incidents, [hidhide], T(8, 10, 0, 0)).ToList();

        c.Should().ContainSingle("seul l'arrêt d'une autre nature, inédit avant l'installation, reste rapproché");
        c[0].ValeurCourante.Should().StartWith("arrêt brutal sans code le 04/08");
    }

    [Fact]
    public void La_version_deja_dans_le_nom_n_est_pas_repetee()
    {
        AvantLIncident.LibelleLogiciel("NVIDIA Logiciel système PhysX 9.26.0703", "9.26.0703").Should().Be("logiciel NVIDIA Logiciel système PhysX 9.26.0703");
        AvantLIncident.LibelleLogiciel("Discord", "1.0.9180").Should().Be("logiciel Discord 1.0.9180");
        AvantLIncident.LibelleLogiciel("Outil", "").Should().Be("logiciel Outil");
    }

    // Lit les vrais journaux d'installation de Windows : un runner de CI n'a pas d'historique de pilotes.
    [Fact, Trait("Category", "Machine")]
    public void Sur_la_vraie_machine_les_journaux_se_lisent_sans_planter()
    {
        var pilotes = AvantLIncident.LirePilotes();
        var logiciels = AvantLIncident.LireLogiciels();

        // OnlyContain échoue sur une liste vide : une machine sans pilote tiers installé n'est pas en faute.
        pilotes.Where(p => !p.Libelle.StartsWith("pilote ")).Should().BeEmpty();
        logiciels.Where(l => !l.AuJour).Should().BeEmpty();
        // Après une réinstallation de Windows, le journal existe mais ne porte que des pilotes Microsoft
        // et des suppressions de l'Assistant de stockage. On n'exige un pilote que si le journal en installe un d'un tiers.
        var journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");
        if (OperatingSystem.IsWindows() && File.Exists(journal)
            && File.ReadLines(journal).Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*inf:\s+Provider:\s+(?!Microsoft)\S")))
            pilotes.Should().NotBeEmpty("le journal installe au moins un pilote tiers");
    }

    // « revenir à la version précédente » contredisait « mets-le à jour » du
    // constat de la version du même pilote. La piste est de changer de version, dans un sens ou dans l'autre.
    [Fact]
    public void La_piste_ne_contredit_pas_la_mise_a_jour_du_pilote()
    {
        var c = AvantLIncident.Juger([new FaitDate(T(9, 2, 5, 9), "arrêt brutal")], AvantLIncident.PilotesInstalles(Journal), T(9, 10, 12, 0)).Single();

        c.Explication.Should().NotContain("revenir à la version précédente, ou désinstaller")
            .And.Contain("changer de version (la précédente, ou une plus récente si elle existe)");
    }

    // Les journaux setupapi gardent des mois d'historique (des dizaines de Mio sur une machine très pilotée) : ils se lisent
    // ligne à ligne, sans être chargés en mémoire. Un fichier verrouillé s'arrête là, sans lever, les autres continuent.
    [Fact]
    public void Un_journal_se_lit_ligne_a_ligne_et_un_journal_verrouille_ne_leve_pas()
    {
        var f = Path.Combine(Path.GetTempPath(), $"setupapi-{Guid.NewGuid():N}.log");
        File.WriteAllLines(f, [">>>  [Device Install]", "ligne"]);
        try
        {
            var lues = AvantLIncident.Lignes(f);
            lues.Should().NotBeAssignableTo<System.Collections.ICollection>("ligne à ligne, jamais une liste en mémoire");
            lues.Should().Equal(">>>  [Device Install]", "ligne");

            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                AvantLIncident.Lignes(f).Should().BeEmpty();
        }
        finally { File.Delete(f); }
    }
}
