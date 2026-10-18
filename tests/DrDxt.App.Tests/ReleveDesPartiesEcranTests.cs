using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// « Tes dernières parties » à l'écran, une ligne par partie, le goulot dit en mots.
public sealed class ReleveDesPartiesEcranTests
{
    [Fact]
    public void Une_partie_se_lit_en_une_ligne_avec_son_goulot_en_mots()
    {
        var p = new PartieRelevee("cod.exe", new DateTimeOffset(2026, 9, 29, 21, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 29, 21, 40, 0, TimeSpan.Zero), 7, 128.4, 94.2, BottleneckState.CpuBoundOrAppBound);

        var ligne = ScanViewModel.LignePartie(p, k => k);

        ligne.Should().Contain("Call of Duty").And.Contain("128").And.Contain("94").And.Contain("Parties.Goulot.Processeur");
    }

    [Fact]
    public void La_frise_dit_la_latence_pc_et_la_part_composee_seulement_quand_elles_sont_lues()
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(30), 7, 128, 94, BottleneckState.CpuBoundOrAppBound);
        ScanViewModel.ResumeCourt(p, k => k).Should().NotContain("Parties.LatencePc").And.NotContain("Parties.Composees");
        ScanViewModel.ResumeCourt(p with { LatencePcMedianMs = 21, PartComposeeMediane = 0.4, HorsCarteMoMax = 900 }, k => k)
            .Should().Contain("Parties.LatencePc").And.Contain("Parties.Composees").And.Contain("Parties.HorsCarte");
    }

    // Le processeur pendant la partie, et la phrase quand il ralentit sous ce que le banc tient.
    [Fact]
    public void La_ligne_dit_le_processeur_en_partie_et_s_il_ralentit()
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 6, 140, 100,
            BottleneckState.CpuBoundOrAppBound, 4300, 118);
        var avecBanc = p with { BancMhz = 5190 };
        var fr = System.Globalization.CultureInfo.CurrentCulture;

        var sansBanc = ScanViewModel.LignePartie(p, k => k);
        sansBanc.Should().Contain("Parties.Processeur").And.NotContain("Parties.Ralentit", "sans banc de l'époque, rien à comparer");
        ScanViewModel.LignePartie(avecBanc, k => k).Should().Contain("Parties.Ralentit").And.NotContain("Parties.RalentitChaleur",
            "sans température lue, la chaleur n'est pas nommée");
        ScanViewModel.LignePartie(avecBanc with { CelsiusMax = 52 }, k => k).Should().NotContain("Parties.RalentitChaleur", "52 °C n'est pas la chaleur");
        ScanViewModel.LignePartie(avecBanc with { CelsiusMax = 96 }, k => k).Should().Contain("Parties.RalentitChaleur");
        ScanViewModel.LignePartie(avecBanc with { MhzMedian = 5100 }, k => k).Should().NotContain("Parties.Ralentit");
        ScanViewModel.LignePartie(avecBanc with { Goulot = BottleneckState.GpuBound }, k => k).Should().NotContain("Parties.Ralentit",
            "limitée par la carte, des cœurs peu chargés tournent bas sans ralentir");
        ScanViewModel.LignePartie(p, k => k == "Parties.Processeur" ? "cpu {0} GHz {1} W" : k)
            .Should().Contain($"cpu {4.3.ToString("0.0", fr)} GHz 118 W");
        sansBanc.Should().NotContain("Parties.Chaleur", "sans HWiNFO, aucune température");
        ScanViewModel.LignePartie(p with { CelsiusMax = 96 }, k => k == "Parties.Chaleur" ? " ({0} °C)" : k)
            .Should().Contain("(96 °C)");
    }

    // Mémoire vidéo saturée pendant la partie, la ligne le dit avec son pic ; en dessous du seuil, rien.
    [Fact]
    public void La_ligne_dit_la_memoire_video_pleine()
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 6, 137, 77, BottleneckState.FramePacing);
        ScanViewModel.LignePartie(p with { VramPourcentMax = 97 }, k => k == "Parties.VramPleine" ? "vram {0} %" : k)
            .Should().Contain("vram 97 %");
        // Sous le seuil, le pic se dit quand même ; un silence se lirait « pas la cause ».
        ScanViewModel.LignePartie(p with { VramPourcentMax = 80 }, k => k == "Parties.Vram" ? "vram lue {0} %" : k)
            .Should().Contain("vram lue 80 %").And.NotContain("Parties.VramPleine");
        ScanViewModel.LignePartie(p, k => k).Should().NotContain("Parties.VramPleine", "non lue n'est pas pleine");
    }

    // Les voisins lourds de la partie, nommés avec leur pic ; sans voisin, rien.
    [Fact]
    public void La_ligne_nomme_les_voisins_lourds()
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 6, 137, 77, BottleneckState.FramePacing);
        ScanViewModel.LignePartie(p with { Voisins = [new VoisinEnPartie("chrome", 55, 3), new VoisinEnPartie("obs64", 31, 2)] },
                k => k == "Parties.Voisins" ? "a cote : {0}" : k)
            .Should().Contain("a cote : chrome (55 %), obs64 (31 %)");
        ScanViewModel.LignePartie(p with { Voisins = [] }, k => k).Should().NotContain("Parties.Voisins");
        ScanViewModel.LignePartie(p, k => k).Should().NotContain("Parties.Voisins");
    }

    // Trois changements sans partie d'avant faisaient trois paragraphes presque identiques.
    // Regroupés, ils faisaient encore un paragraphe de quatre lignes. Les changements
    // pas encore jugés passent dans un tableau (changement · date · en jeu) ; les phrases ne gardent que les jugés.
    [Fact]
    public void Les_changements_pas_encore_juges_passent_en_tableau()
    {
        var t = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(2));
        var sansAvant = new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, null, 0, 0, null, null, null, null);
        var peuAvant = new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, "cod.exe", 1, 2, null, null, null, null);
        var attend = new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, "cod.exe", 4, 1, null, null, null, null);
        var juge = new BilanDuChangement(VerdictEnJeu.Mieux, "cod.exe", 3, 3, 90, 100, 140, 150);
        string L(string k) => k switch
        {
            "Changement.Tableau.SansAvant" => "jamais", "Changement.Tableau.PeuAvant" => "jamais {0}/{1}",
            "Changement.Tableau.Attend" => "attend {0} partie(s)", "Format.Jour" => "dd/MM", _ => k,
        };
        (Changement, BilanDuChangement)[] tous = [(new Changement(t, "la carte"), sansAvant), (new Changement(t, "le jeu"), peuAvant),
            (new Changement(t.AddHours(-4), "le BIOS"), attend), (new Changement(t.AddDays(-1), "HAGS"), juge)];

        ScanViewModel.LignesDesChangements(tous, L).Should().ContainSingle("seul le changement jugé reste une phrase");
        ScanViewModel.TableauDesChangementsEnAttente(tous, L).Should().Equal(
            new LigneEnAttente("la carte", "04/10", "jamais"),
            new LigneEnAttente("le jeu", "04/10", "jamais 1/3"),
            new LigneEnAttente("le BIOS", "04/10", "attend 2 parties"));
    }

    // La mesure qui juge se dit : la latence pour un levier de latence qui la porte, sinon les 1 % bas, et c'est dit aussi.
    [Fact]
    public void Un_changement_de_latence_dit_sur_quelle_mesure_il_est_juge()
    {
        var hags = new Changement(new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero), "HAGS", Cle: "windows.hags:on");
        string L(string k) => k switch
        {
            "Changement.JugeLatence" => "{0}|{2}|{4}>{5} ms|{6}/{7}",
            "Changement.Juge" => "{0}|{2}|{4}>{5}",
            _ => k,
        };

        ScanViewModel.LigneChangement(hags, new BilanDuChangement(VerdictEnJeu.Mieux, "cod.exe", 3, 3, 95, 81, 120, 118,
                SurLaLatence: true, LatenceAvant: 30, LatenceApres: 20), L)
            .Should().Be("HAGS|Changement.Mieux|30>20 ms|3/3");
        ScanViewModel.LigneChangement(hags, new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 95, 81, 120, 118), L)
            .Should().Be("HAGS|Changement.Pire|95>81 Changement.JugeSurLesImages");
    }

    // Avant et après mesurés de deux façons : le joueur lit « pas comparable », jamais « pas de différence ».
    [Fact]
    public void Un_changement_pas_comparable_le_dit()
    {
        var c = new Changement(new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero), "le BIOS");

        ScanViewModel.LigneChangement(c, new BilanDuChangement(VerdictEnJeu.PasComparable, "cod.exe", 3, 3, null, null, null, null),
                k => k == "Changement.PasComparable" ? "{0}|{1}|{2}" : k == "Format.Jour" ? "dd/MM" : k)
            .Should().Be("le BIOS|04/10|3");
    }

    // « Pas comparable » vient de l'avant OU de l'après (MemeEstimateur) : la phrase ne désigne pas un côté.
    [Theory]
    [InlineData("fr-FR", "d'avant")]
    [InlineData("en-US", "before")]
    public void Pas_comparable_n_accuse_pas_un_cote(string langue, string cote)
        => new DrDxt.Infrastructure.Localization.ResxLocalizer().Get("Changement.PasComparable", System.Globalization.CultureInfo.GetCultureInfo(langue))
            .Should().NotContain(cote);

    // Un changement daté se dit jugé, avec ses médianes, ou avec ce qui manque pour le juger.
    [Fact]
    public void Un_changement_juge_dit_son_verdict_et_ses_chiffres()
    {
        var c = new Changement(new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero), "le BIOS");
        var juge = ScanViewModel.LigneChangement(c,
            new BilanDuChangement(VerdictEnJeu.Mieux, "cod.exe", 3, 4, 90, 105, 130, 150), k => k == "Changement.Juge" ? "{0}|{2}|{3}|{4}>{5}|{6}>{7}|{8}/{9}" : k);
        juge.Should().Be("le BIOS|Changement.Mieux|Call of Duty|90>105|130>150|3/4");

        var attente = ScanViewModel.LigneChangement(c,
            new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, "cod.exe", 2, 1, null, null, null, null), k => k == "Changement.PasEncore" ? "{0}|{2}|{3}|{4}" : k);
        attente.Should().Be("le BIOS|1|2|3");
        ScanViewModel.LigneChangement(c, new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, null, 0, 0, null, null, null, null), k => k)
            .Should().Be("Changement.SansAvant", "sans partie d'avant, rien ne permettra jamais de le juger");
    }

    [Theory]
    [InlineData(BottleneckState.GpuBound, "Parties.Goulot.Carte")]
    [InlineData(BottleneckState.UnknownPartial, "Parties.Goulot.Inconnu")]
    // Une partie jugée « cadence irrégulière » (137 FPS, 1 % low 77 relevés sur la carte Halloween) ne doit pas
    // s'afficher « limite non mesurée ». Chaque verdict mesuré a ses mots.
    [InlineData(BottleneckState.FramePacing, "Parties.Goulot.Cadence")]
    [InlineData(BottleneckState.CpuBoundStrong, "Parties.Goulot.Processeur")]
    [InlineData(BottleneckState.ThermalOrPowerThrottle, "Parties.Goulot.CarteBridee")]
    [InlineData(BottleneckState.CappedOrVsyncLimited, "Parties.Goulot.Plafond")]
    [InlineData(BottleneckState.RamCommitPressure, "Parties.Goulot.Memoire")]
    [InlineData(BottleneckState.LatencyOnly, "Parties.Goulot.Latence")]
    public void Chaque_goulot_a_ses_mots(BottleneckState goulot, string cle)
        => ScanViewModel.LignePartie(new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 3, 100, 80, goulot), k => k)
            .Should().Contain(cle);

    // La carte à son pic, quand elle est lue ; non lue, rien n'est écrit (pas « 0 °C »).
    [Fact]
    public void La_ligne_dit_la_temperature_de_la_carte_seulement_lue()
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 6, 140, 100, BottleneckState.GpuBound);

        ScanViewModel.LignePartie(p, k => k).Should().NotContain("Parties.Carte");
        ScanViewModel.LignePartie(p with { CarteCelsiusMax = 71.6 }, k => k == "Parties.Carte" ? "carte {0}" : k)
            .Should().Contain("carte 72");
    }

    // Le jeu sur une puce intégrée alors qu'une vraie carte est là, dit sur la ligne avec le geste.
    [Fact]
    public void Un_jeu_sur_la_puce_integree_se_dit_sur_sa_ligne()
    {
        var p = new PartieRelevee("jeu.exe", DateTimeOffset.Now, DateTimeOffset.Now, 3, 40, 25, BottleneckState.GpuBound,
            CarteDuJeu: "Intel UHD", CarteDedieeIgnoree: "RTX 3060");
        ScanViewModel.LignePartie(p, k => k == "Parties.CarteIgnoree" ? "ignoree {0} {1} {2}" : k).Should().Contain("ignoree Intel UHD RTX 3060 jeu");
        ScanViewModel.LignePartie(p with { CarteDedieeIgnoree = null }, k => k).Should().NotContain("Parties.CarteIgnoree");
    }

    // Le ping sur la ligne de la partie, et la route qui se dégrade dite avec où elle se dégrade.
    [Fact]
    public void Le_ping_se_lit_sur_la_partie_et_la_route_qui_se_degrade_dit_ou()
    {
        var t = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
        PartieRelevee P(int j, double net, double box) => new("cod.exe", t.AddDays(j), t.AddDays(j).AddMinutes(30), 4, 140, 90,
            BottleneckState.CpuBoundOrAppBound, PingInternetMedian: net, PingBoxMedian: box, PertesInternetMax: 0);
        string L(string k) => k switch { "Parties.Ping" => "ping {0} box {1} pertes {2}", "Parties.Route.Loin" => "loin {0} {1} {2}", "Parties.Route.Box" => "box {0} {1} {2}", _ => k };

        ScanViewModel.LignePartie(P(1, 12, 2), L).Should().Contain("ping 12 box 2 pertes 0");
        ScanViewModel.LigneRoute([P(1, 12, 2), P(2, 13, 2), P(3, 12, 2), P(4, 25, 2), P(5, 27, 2), P(6, 26, 2)], L).Should().Be("loin 12 26 3");
        ScanViewModel.LigneRoute([P(1, 12, 2), P(2, 13, 2), P(3, 12, 2), P(4, 25, 9), P(5, 27, 9), P(6, 26, 9)], L).Should().Be("box 12 26 3");
        ScanViewModel.LigneRoute([P(1, 12, 2)], L).Should().BeNull();
    }

    // Les overlays ouverts sur la ligne de la partie, et leur coût seulement quand il est mesuré.
    [Fact]
    public void Les_overlays_se_lisent_sur_la_partie_et_leur_cout_n_apparait_qu_avec_trois_parties_de_chaque_cote()
    {
        PartieRelevee P(double fps, params string[] o) => new("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 3, fps, fps * 0.6,
            BottleneckState.CpuBoundOrAppBound, Overlays: o);
        string L(string k) => k switch
        {
            "Parties.Overlays" => "overlays {0}",
            "Parties.OverlayEcart" => "ecart {0} {1} {2} {3} {4} {5} {6}",
            "Parties.OverlaySansComparaison" => "attente {0} {1} {2} {3}",
            _ => k,
        };

        ScanViewModel.LignePartie(P(140, "Steam"), L).Should().Contain("overlays Steam");
        ScanViewModel.LignePartie(P(140), L).Should().NotContain("overlays");
        ScanViewModel.LignesOverlays([P(140, "Steam"), P(150, "Steam"), P(170)], L).Should().Equal("attente Steam Call of Duty 2 3");
        ScanViewModel.LignesOverlays([P(140, "Steam"), P(150, "Steam"), P(145, "Steam"), P(170), P(160), P(165)], L)
            .Should().Equal($"ecart Steam Call of Duty {145} {165} 3 3 Parties.OverlayEcart.Pire");
        ScanViewModel.LignesOverlays([P(140), P(150)], L).Should().BeEmpty("aucun overlay vu, rien à dire");
    }
}
