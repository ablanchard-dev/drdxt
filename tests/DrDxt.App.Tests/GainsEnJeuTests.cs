using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// `GainCumule` n'était appelé que par un outil de développement ; l'App jugeait chaque
/// changement sur les parties sans jamais dire ce que l'ensemble avait rapporté. La carte des parties commence par cette
/// ligne : les gains prouvés (séparation nette sur 3 parties de chaque côté), composés et non additionnés ; ce qui n'a
/// rien changé ; ce qui attend encore des parties.
/// Bruit des bornes, autre jeu dit, fenêtres bornées et bilans figés.
/// </summary>
public class GainsEnJeuTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static Changement C(string libelle) => new(T, libelle, null, null);
    private static BilanDuChangement Mieux(double avant, double apres, double bornes, string jeu = "cod.exe") =>
        new(VerdictEnJeu.Mieux, jeu, 3, 3, avant, apres, 130, 135, bornes);
    private static BilanDuChangement Rien() => new(VerdictEnJeu.PasDeDifference, "cod.exe", 3, 3, 80, 80, 130, 130);
    private static BilanDuChangement PasJuge() => new(VerdictEnJeu.PasEncoreJuge, "cod.exe", 3, 1, 80, 82, 130, 131);

    private static string Loc(string k) => k switch
    {
        "Parties.Gains" => "{0} | {1} sans effet | {2} pas encore jugés",
        "Parties.Gains.Total" => "{0:+0.0;-0.0} % de 1 % low (au moins {2:+0.0;-0.0} %, {1} prouvés)",
        "Parties.Gains.Aucun" => "aucun gain prouvé",
        "Parties.Gains.AutreJeu" => "| {0} sur un autre jeu",
        _ => k,
    };

    private static string F(double v) => v.ToString("+0.0;-0.0", System.Globalization.CultureInfo.CurrentCulture);

    [Fact]
    public void Sans_changement_rien_n_est_dit()
        => ScanViewModel.LigneDesGains([], Loc).Should().BeNull();

    [Fact]
    public void Deux_gains_prouves_se_composent_dit_au_moins_et_le_reste_est_compte()
    {
        var ligne = ScanViewModel.LigneDesGains(
            [(C("HAGS"), Mieux(80, 88, 5)), (C("VRR"), Mieux(88, 92.4, 2)), (C("mode Jeu"), Rien()), (C("BIOS"), PasJuge())], Loc)!;

        // 1,10 × 1,05 − 1 = 15,5 %, pas 15 %. « Au moins » prend le pire cas de chaque mesure, ses bornes, composées de même :
        // 1,05 × 1,02 − 1 = 7,1 %. Une somme quadratique de ces écarts donnerait 9,3 %, plus que le vrai pire cas.
        ligne.Should().StartWith(F(15.5) + " % de 1 % low (au moins " + F(7.1) + " %").And.EndWith("2 prouvés) | 1 sans effet | 1 pas encore jugés");
    }

    [Fact]
    public void Sans_gain_prouve_la_ligne_le_dit()
        => ScanViewModel.LigneDesGains([(C("BIOS"), PasJuge())], Loc)
            .Should().Be("aucun gain prouvé | 0 sans effet | 1 pas encore jugés");

    // Un gain prouvé sur un autre jeu (autre instrument) ne s'additionne pas, mais il est dit.
    [Fact]
    public void Un_gain_sur_un_autre_jeu_est_compte_et_pas_tu()
        => ScanViewModel.LigneDesGains(
                [(C("HAGS"), Mieux(80, 88, 5)), (C("VRR"), Mieux(80, 84, 2)), (C("mode Jeu"), Mieux(100, 110, 4, "valorant.exe"))], Loc)
            .Should().EndWith("| 1 sur un autre jeu");

    private static PartieRelevee P(int jour, double low1) =>
        new("cod.exe", new DateTimeOffset(2026, 10, jour, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, jour, 20, 30, 0, TimeSpan.Zero),
            4, 130, low1, DrDxt.Core.BottleneckState.CpuBoundOrAppBound);

    // A (le 4) améliore (70-72 ⇒ 73-75), puis B (le 8) dégrade (⇒ 60-62). Sans borne, les parties d'après B entraient
    // dans l'après de A et le rendaient « pas de différence » : l'effet de B se mêlait à celui de A.
    [Fact]
    public void L_apres_d_un_changement_s_arrete_au_changement_suivant()
    {
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 73), P(6, 74), P(7, 75), P(9, 60), P(10, 61), P(11, 62) };
        var a = new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "A");
        var b = new Changement(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "B");

        var bilans = ScanViewModel.BilansDesChangements([b, a], parties, journal: null);

        bilans.Single(x => x.C.Libelle == "A").B.Verdict.Should().Be(VerdictEnJeu.Mieux, "ses parties d'après sont celles du 5 au 7");
        bilans.Single(x => x.C.Libelle == "B").B.Verdict.Should().Be(VerdictEnJeu.Pire);
    }

    // B, allumé hors de DrDXT, n'est relevé qu'au scan du 12 : son heure exacte se situe entre le 8 (Depuis, le scan
    // d'avant) et le 12 (Quand). Les parties du 9 au 11 ont pu être jouées sous B : elles ne comptent d'aucun côté, pas dans
    // l'après de A, où elles feraient porter à A le coût de B (et le retireraient).
    [Fact]
    public void L_apres_d_un_changement_s_arrete_au_depuis_du_suivant()
    {
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 73), P(6, 74), P(7, 75), P(9, 60), P(10, 61), P(11, 62) };
        var a = new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "A");
        var b = new Changement(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), "B",
            Depuis: new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

        var bilanA = ScanViewModel.BilansDesChangements([b, a], parties, journal: null).Single(x => x.C.Libelle == "A").B;

        bilanA.Verdict.Should().Be(VerdictEnJeu.Mieux, "ses parties d'après sont celles du 5 au 7");
        bilanA.Apres.Should().Be(3);
    }

    // Un bilan figé est repris tel quel, même quand les parties d'avant ont quitté l'anneau des 20.
    [Fact]
    public void Un_bilan_fige_ne_se_rejuge_pas()
    {
        var fige = Mieux(70, 80, 3);
        var c = new Changement(T, "HAGS", Fige: fige);

        ScanViewModel.BilansDesChangements([c], [], journal: null).Single().B.Should().Be(fige);
    }

    // 70-72, A, 80-82, B, 84-86. Sans borne basse, l'avant de B mêlait l'ère d'avant A
    // (médiane 76, +11,8 %) et le total comptait le gain de A deux fois. Borné, l'avant de B est l'ère de A (médiane 81).
    [Fact]
    public void L_avant_d_un_changement_commence_au_changement_precedent()
    {
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 80), P(6, 81), P(7, 82), P(9, 84), P(10, 85), P(11, 86) };
        var a = new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "A");
        var b = new Changement(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "B");

        var bilanB = ScanViewModel.BilansDesChangements([a, b], parties, journal: null).Single(x => x.C.Libelle == "B").B;

        bilanB.Low1Avant.Should().Be(81);
        bilanB.Avant.Should().Be(3);
    }

    // 3 contre 3 arrive par hasard une fois sur dix. Tant que la fenêtre est ouverte et que
    // le relevé n'est pas plein, une 4e partie peut encore corriger : rien n'est figé. Fenêtre close : figé.
    [Fact]
    public void Un_bilan_ne_se_fige_que_quand_plus_rien_ne_peut_le_corriger()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fige-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var journal = new JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            journal.Ajouter(new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "A"));
            var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 80), P(6, 81), P(7, 82) };

            ScanViewModel.BilansDesChangements(journal.Lire(), parties, journal).Single().B.Verdict.Should().Be(VerdictEnJeu.Mieux);
            journal.Lire().Single().Fige.Should().BeNull("la fenêtre de A est encore ouverte");

            journal.Ajouter(new Changement(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "B"));
            ScanViewModel.BilansDesChangements(journal.Lire(), parties, journal);
            journal.Lire().Single(c => c.Libelle == "A").Fige.Should().NotBeNull("B ferme la fenêtre de A");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
    // Une capture ratée plus récente que la dernière partie se dit ; plus ancienne, elle
    // a été suivie d'une partie relevée et ne dit plus rien.
    [Fact]
    public void Une_capture_ratee_depuis_la_derniere_partie_se_dit()
    {
        var echec = new PartiesMemorisees.EchecDeCapture(new DateTimeOffset(2026, 10, 8, 21, 0, 0, TimeSpan.Zero), "cod.exe : service refusé");

        ScanViewModel.LigneEchec(echec, [P(5, 80)], Loc).Should().Be("Parties.CaptureRatee");
        ScanViewModel.LigneEchec(echec, [P(9, 80)], Loc).Should().BeNull();
        ScanViewModel.LigneEchec(null, [], Loc).Should().BeNull();
    }

    // Le service de mesure de DrDXT arrêté (démarrage à la demande, après un redémarrage ou une mise à jour) : jouer n'y
    // change rien, le relevé sans droits ne peut pas le relancer. « DrDXT réessaie à ta prochaine partie » était faux.
    [Theory]
    [InlineData("cod.exe : 0 image(s) DXGI pour le processus 42 (PresentMon avant lui : MEAS.PMON_UNAVAIL)")]
    [InlineData("cod.exe : service injoignable (MEAS.PMON_UNAVAIL)")]
    public void Une_capture_ratee_faute_du_service_de_mesure_le_dit(string raison)
    {
        var echec = new PartiesMemorisees.EchecDeCapture(new DateTimeOffset(2026, 10, 8, 21, 0, 0, TimeSpan.Zero), raison);

        ScanViewModel.LigneEchec(echec, [P(5, 80)], Loc).Should().Be("Parties.CaptureRateeService");
    }

    // La raison notée garde le code du service injoignable même quand son message ne le porte pas : c'est lui qui choisit
    // la phrase ci-dessus.
    [Fact]
    public void La_raison_d_une_capture_sans_service_porte_son_code()
    {
        ScanViewModel.RaisonDuRate(DrDxt.Core.ErrorCodes.PmonUnavailable, "service absent").Should().Contain(DrDxt.Core.ErrorCodes.PmonUnavailable);
        ScanViewModel.RaisonDuRate(DrDxt.Core.ErrorCodes.NoFrames, "0 image").Should().Be("0 image");
    }

    // Le code d'erreur brut (« powershell.exe : 0 image(s) DXGI… MEAS.NO_FRAMES »)
    // ne s'affiche plus au joueur ; la phrase dit quoi faire. La raison reste dans le fichier de l'échec.
    [Theory]
    [InlineData("fr", "ta prochaine partie")]
    [InlineData("en", "your next game")]
    public void Une_capture_ratee_se_dit_sans_son_code_d_erreur(string culture, string attendu)
    {
        var avant = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var echec = new PartiesMemorisees.EchecDeCapture(new DateTimeOffset(2026, 10, 8, 7, 19, 0, TimeSpan.Zero),
                "powershell.exe : 0 image(s) DXGI pour le processus 61740 (PresentMon avant lui : MEAS.NO_FRAMES)");
            var localise = new DrDxt.Infrastructure.Localization.ResxLocalizer();

            var ligne = ScanViewModel.LigneEchec(echec, [P(5, 80)], k => localise.Get(k, System.Globalization.CultureInfo.CurrentUICulture));

            ligne.Should().Contain(attendu).And.NotContain("MEAS.").And.NotContain("powershell");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = avant; }
    }
    // Relevé plein (20 parties), c'est l'état normal d'un joueur installé. A ne se fige que si
    // la prochaine partie à sortir est une partie d'avant A ; sinon une 4e partie peut encore corriger son verdict.
    [Fact]
    public void Releve_plein_ne_fige_que_si_la_prochaine_partie_perdue_est_d_avant()
    {
        static Changement Ch(int jour, string nom) => new(new DateTimeOffset(2026, 10, jour, 12, 0, 0, TimeSpan.Zero), nom);
        var apres = Enumerable.Range(7, 17).Select(j => P(j, 80)).ToList();

        var perdAvant = new[] { P(3, 70), P(4, 71), P(5, 72) }.Concat(apres).ToList();
        perdAvant.Should().HaveCount(ReleveDesParties.PartiesGardees);
        Fige(perdAvant, Ch(6, "A")).Should().NotBeNull("la partie du 3, d'avant A, sort à la prochaine partie");

        var perdPlusVieux = new[] { P(1, 60), P(3, 70), P(4, 71), P(5, 72) }.Concat(apres.Take(16)).ToList();
        perdPlusVieux.Should().HaveCount(ReleveDesParties.PartiesGardees);
        Fige(perdPlusVieux, Ch(2, "Z"), Ch(6, "A")).Should().BeNull("la partie qui sort est d'avant Z : A peut encore se corriger");
    }

    private static BilanDuChangement? Fige(IReadOnlyList<PartieRelevee> parties, params Changement[] changements)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fige20-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var journal = new JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            foreach (var c in changements) journal.Ajouter(c);
            ScanViewModel.BilansDesChangements(journal.Lire(), parties, journal);
            return journal.Lire().Single(c => c.Libelle == "A").Fige;
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
    // « au démarrage du PC » se recalcule à chaque lecture ; deux changements du même
    // redémarrage tombent à quelques ms l'un de l'autre. Bornés l'un par l'autre, aucun n'avait de partie d'un côté.
    [Fact]
    public void Deux_changements_a_quelques_ms_sont_du_meme_instant_et_se_jugent()
    {
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 80), P(6, 81), P(7, 82) };
        var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var hags = new Changement(quand, "HAGS");
        var bios = new Changement(quand.AddMilliseconds(9), "BIOS");

        ScanViewModel.BilansDesChangements([hags, bios], parties, journal: null)
            .Should().HaveCount(2).And.OnlyContain(x => x.B.Verdict == VerdictEnJeu.Mieux);
    }

    // Un même clic, l'un retiré (bilan figé), l'autre gardé et rejugé ensuite avec d'autres
    // parties : deux bilans pour une seule mesure. Le figé fait foi, la mesure ne compte qu'une fois.
    [Fact]
    public void Un_instant_au_bilan_fige_ne_compte_qu_une_mesure()
    {
        var fige = Mieux(80, 88, 5);
        var retire = new Changement(T, "HAGS", Fige: fige);
        var garde = new Changement(T.AddMilliseconds(3), "fichier d'échange");

        ScanViewModel.LigneDesGains([(retire, fige), (garde, Mieux(80, 92, 6))], Loc).Should().Contain("1 prouvés");
    }

    // Un pilote mis à jour le 8 borne l'après du levier du 4 ; son gain n'est pas celui du levier.
    [Fact]
    public void Une_mise_a_jour_de_la_machine_borne_l_apres_d_un_changement_de_DrDXT()
    {
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 70), P(6, 72), P(7, 71), P(9, 90), P(10, 91), P(11, 92) };
        var a = new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "A");
        var pilote = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        ScanViewModel.BilansDesChangements([a], parties, journal: null).Single().B.Verdict.Should().Be(VerdictEnJeu.PasDeDifference);
        ScanViewModel.BilansDesChangements([a], parties, journal: null, misesAJour: [pilote])
            .Single().B.Apres.Should().Be(3, "les parties du 9 au 11 sont celles du pilote");
    }

    // Les parties d'un essai en alternance ne jugent pas les autres changements.
    [Fact]
    public void Les_parties_d_un_essai_ne_comptent_pas_pour_les_autres_changements()
    {
        var essai = AlternanceDesEssais.Marque("gpu.power_limit", true);
        var parties = new[] { P(1, 70), P(2, 71), P(3, 72), P(5, 80) with { Essai = essai }, P(6, 81) with { Essai = essai }, P(7, 82) with { Essai = essai } };

        ComparaisonDesParties.Bilan(parties, new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)).Apres.Should().Be(0);
        ComparaisonDesParties.Juger(parties, new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "cod.exe")
            .Should().Be(VerdictEnJeu.PasEncoreJuge);
    }

    // Un gain et une perte prouvés qui s'annulent : le total (+0,6 %) est sous son bruit, et la ligne l'annonçait quand
    // même avec « au moins -7,0 % ». Sous son bruit, un total ne s'annonce pas ; c'est la règle de GainCumule.Phrase.
    [Fact]
    public void Un_total_sous_son_bruit_ne_s_annonce_pas()
    {
        var pire = new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 88, 80.5, 130, 125, -3);
        var ligne = ScanViewModel.LigneDesGains([(C("HAGS"), Mieux(80, 88, 5)), (C("VRR"), pire)], Loc)!;

        ligne.Should().NotContain("au moins -").And.Contain("Parties.Gains.SousLeBruit");
    }

    // « Au moins » compose la borne de chaque mesure, sa pire paire : pour une perte, plus bas qu'elle, une valeur que les
    // parties ont prise, jamais extrapolée.
    [Fact]
    public void Au_moins_prend_la_pire_paire_d_une_perte()
    {
        var pire = new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 88, 80.5, 130, 125, -14);
        var ligne = ScanViewModel.LigneDesGains([(C("HAGS"), Mieux(80, 88, 5)), (C("VRR"), Mieux(88, 105.6, 15)), (C("BIOS"), pire)], Loc)!;

        ligne.Should().Contain("(au moins " + F((1.05 * 1.15 * 0.86 - 1) * 100) + " %");
    }

    // Un bilan figé avant que la borne d'une perte soit sa pire paire porte la paire la plus proche de zéro. « Au moins » n'en
    // fait pas un plancher : la perte y compte pour son écart mesuré, jamais pour 2 × écart - borne.
    [Fact]
    public void Une_perte_figee_avec_l_ancienne_borne_compte_pour_son_ecart()
    {
        var pire = new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 102, 92, 130, 125, -2);
        var ligne = ScanViewModel.LigneDesGains([(C("HAGS"), Mieux(80, 100, 15)), (C("BIOS"), pire)], Loc)!;

        ligne.Should().Contain("(au moins " + F((1.15 * (92.0 / 102) - 1) * 100) + " %");
    }
}
