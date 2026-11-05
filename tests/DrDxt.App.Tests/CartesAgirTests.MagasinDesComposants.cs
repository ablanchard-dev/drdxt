using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Le magasin des composants : la réparation par la console, sa source et le journal CBS.</summary>
public sealed partial class CartesAgirTests
{
    // ── Réparer Windows (DISM puis sfc, console visible) ─────────────────────────
    private static readonly RepairFinding MagasinReparable = new(
        "STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious,
        "DISM /CheckHealth signale des composants réparables.", RepairAction.Recommandation);

    [Fact]
    public async Task Un_magasin_reparable_arme_la_carte_et_le_maintien_ouvre_la_console()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        System.Diagnostics.ProcessStartInfo? lance = null;
        vm.LancerConsole = psi => { lance = psi; return new TaskCompletionSource().Task; };
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ReparationVisible.Should().BeTrue();
        vm.CarteWindowsVisible.Should().BeTrue();
        vm.LancerReparationCommand.Execute(null);

        lance.Should().NotBeNull();
        lance!.Verb.Should().Be("runas");
        vm.ReparationLancee.Should().BeTrue();
        vm.ReparationAOuvrir.Should().BeFalse("une console suffit : le bouton ne la rouvre pas");
    }

    // Critique B10 (passage 3) : la console fermée, rien ne revenait dans DrDXT ; le joueur ne savait
    // pas si la réparation avait marché. À la fermeture, l'état de Windows est relu (lecture) et la carte le dit.
    [Theory]
    [InlineData("sain", RepairStatus.ExpectedDefault, "Reparation.Windows.Terminee.Sain", false)]
    [InlineData("abîmé, réparable", RepairStatus.Suspicious, "Reparation.Windows.Terminee.Abime", true)]
    [InlineData(null, RepairStatus.Unknown, "Reparation.Windows.Terminee.NonRelu", true)]
    public async Task La_console_fermee_la_carte_dit_ce_que_la_relecture_trouve(string? valeur, RepairStatus statut, string attendu, bool relancable)
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var console = new TaskCompletionSource();
        vm.LancerConsole = _ => console.Task;
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", valeur, "sain", statut, "relu par DISM /CheckHealth");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LancerReparationCommand.Execute(null);

        vm.EtatReparation.Should().Be("Reparation.Windows.Lancee", "témoin : pendant la réparation, l'état en cours");
        console.SetResult();
        await vm.FinDeReparation!;

        vm.EtatReparation.Should().Be(attendu);
        vm.ReparationAOuvrir.Should().Be(relancable);
    }

    // Le joueur lit le constat, pas la carte Agir : le constat porte lui-même le bouton qui répare, sans helper (la console
    // demande l'UAC). Sans lui, la seule voie écrite sous le constat est le terminal, où un joueur non technique s'arrête.
    [Theory]
    [InlineData(RepairStatus.ExpectedDefault, true)]
    [InlineData(RepairStatus.Suspicious, false)]
    public async Task Le_constat_reparable_porte_le_bouton_qui_repare_et_dit_ce_que_la_relecture_trouve(RepairStatus relu, bool sain)
    {
        var vm = Vm(new[] { MagasinReparable with { Remede = "1) Terminal." } }, helper: null);
        var console = new TaskCompletionSource();
        vm.LancerConsole = _ => console.Task;
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", "x", "sain", relu, "relu");
        await vm.ScanCommand.ExecuteAsync(null);

        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
        var fin = geste.FaireCommand.ExecuteAsync(null);
        vm.ReparationLancee.Should().BeTrue("le bouton du constat ouvre la même console que la carte");
        geste.EnCours.Should().BeTrue("tant que la console est ouverte, la réparation n'est pas finie");

        console.SetResult();
        await fin;
        geste.Fait.Should().Be(sain);
        geste.AEchoue.Should().Be(!sain, "encore abîmé après la console : le bouton ne dit pas « fait »");
        geste.PeutAnnuler.Should().BeFalse("une réparation de Windows ne se défait pas");
    }

    // Le bouton est annoncé avant les étapes à la main : lues d'abord, elles envoient le joueur au terminal.
    [Fact]
    public async Task Le_remede_annonce_le_bouton_avant_les_etapes_du_terminal()
    {
        var vm = Vm(new[] { MagasinReparable with { Remede = Docteur.T("WinSante.Reparable.Remede") } }, helper: null);
        await vm.ScanCommand.ExecuteAsync(null);

        var ligne = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes);
        ligne.RemedeLisible.Should().StartWith(Docteur.T("Carte.BoutonAvantLeTerminal"));
        ligne.RemedeLisible.Should().Contain("Terminal", "les étapes à la main restent, en secours");
    }

    // Sans bouton, rien n'est annoncé : la phrase ne promet jamais un geste que la carte ne montre pas.
    [Fact]
    public void Sans_geste_le_remede_reste_tel_quel()
    {
        var remede = Docteur.T("WinSante.Reparable.Remede");
        var entree = new EntreeCarte("windows", Severity.Minor, "t", "d", "", "", false, RemedeDuConstat: remede);
        entree.RemedeLisible.Should().Be(remede);
    }

    // Sans source de réparation, le geste de la carte devient la réinstallation de Windows : ouvrir la page Récupération
    // ne la fait pas. Le constat ne porte alors aucun bouton, qui se dirait « fait » sur une page ouverte.
    [Fact]
    public async Task Sans_source_le_constat_ne_porte_pas_de_bouton_qui_se_dirait_fait()
    {
        var vm = Vm(new[] { MagasinReparable with { Remede = "1) Terminal." } }, helper: null);
        vm.CorruptionSansSource = () => DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller;
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ReparationVisible.Should().BeTrue("la carte Agir garde la réinstallation, avec ses mots");
        vm.Domaines.SelectMany(d => d.Lignes).Should().NotContain(l => l.AGestes);
    }

    // Une mesure ou une écriture en cours grise la carte Agir : le bouton du constat n'ouvre pas la console élevée à côté.
    [Fact]
    public async Task Pendant_une_operation_longue_le_bouton_du_constat_ne_lance_rien()
    {
        var vm = Vm(new[] { MagasinReparable with { Remede = "1) Terminal." } }, helper: null);
        var lances = 0;
        vm.LancerConsole = _ => { lances++; return new TaskCompletionSource().Task; };
        await vm.ScanCommand.ExecuteAsync(null);
        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();

        vm.RemesureCpuEnCours = true;
        await geste.FaireCommand.ExecuteAsync(null);

        lances.Should().Be(0);
        geste.Fait.Should().BeFalse();
        geste.AEchoue.Should().BeTrue();
    }

    // Une précondition que DrDXT ne fait pas (retirer d'abord les cartes déconnectées) : la phrase ne renvoie pas au bouton,
    // qui échouerait sans elle.
    [Fact]
    public void Un_remede_a_precondition_n_annonce_pas_le_bouton()
    {
        var remede = Docteur.T("MP.DAbord", "pnputil /delete-driver oem98.inf");
        var geste = new GesteDuDocteur("t", "d", () => Task.FromResult(Result.Ok()));
        var entree = new EntreeCarte("graphique", Severity.Minor, "t", "d", "", "", false, RemedeDuConstat: remede, Gestes: [geste]);
        entree.RemedeLisible.Should().Be(remede);
    }

    [Fact]
    public async Task Le_bouton_du_constat_refuse_par_l_uac_dit_l_echec_sans_rien_lancer()
    {
        var vm = Vm(new[] { MagasinReparable with { Remede = "1) Terminal." } }, helper: null);
        vm.LancerConsole = _ => throw new System.ComponentModel.Win32Exception(1223);
        await vm.ScanCommand.ExecuteAsync(null);

        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
        await geste.FaireCommand.ExecuteAsync(null);

        geste.AEchoue.Should().BeTrue();
        geste.Fait.Should().BeFalse();
    }

    // Un magasin sain ou non lu n'a rien à réparer : aucun bouton, jamais un geste sur une lecture qui ne l'a pas demandé.
    [Theory]
    [InlineData(RepairStatus.ExpectedDefault)]
    [InlineData(RepairStatus.Unknown)]
    public async Task Un_magasin_sain_ou_non_lu_ne_porte_aucun_bouton(RepairStatus statut)
    {
        var vm = Vm(new[] { MagasinReparable with { Statut = statut, Remede = null } }, helper: null);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.Domaines.SelectMany(d => d.Lignes).Should().NotContain(l => l.AGestes);
    }

    [Fact]
    public async Task Sans_constat_reparable_la_carte_n_existe_pas()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.ReparationVisible.Should().BeFalse();
        vm.CarteWindowsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Une_console_qui_ne_s_ouvre_pas_le_dit()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        vm.LancerConsole = _ => null;
        await vm.ScanCommand.ExecuteAsync(null);

        vm.LancerReparationCommand.Execute(null);

        vm.ReparationLancee.Should().BeFalse();
        vm.EtatReparation.Should().Be("Reparation.Windows.NonLancee", "le clic ne reste pas muet");
    }

    [Fact]
    public async Task Une_fenetre_UAC_refusee_le_dit_sans_panne()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        vm.LancerConsole = _ => throw new System.ComponentModel.Win32Exception(1223);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LancerReparationCommand.Execute(null);
        vm.ReparationLancee.Should().BeFalse();
        vm.ErreurReparation.Should().Be("Reparation.Windows.Refusee");
    }

    // Vu sur la machine de référence, RestoreHealth échoue (0x800f0915) et ce qui est abîmé n'est que des versions
    // remplacées. Relancer la même réparation tournait en rond : la carte le dit et propose le nettoyage.
    [Fact]
    // Même quand tout ce qui est abîmé est une version remplacée, le geste est la réinstallation de Microsoft (elle garde
    // fichiers, applications et réglages), jamais le nettoyage irréversible.
    public async Task Une_reparation_sans_source_ouvre_toujours_la_reinstallation_jamais_ResetBase()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var lances = new List<System.Diagnostics.ProcessStartInfo>();
        var console = new TaskCompletionSource();
        vm.LancerConsole = psi => { lances.Add(psi); return psi.FileName.StartsWith("ms-settings") ? null : console.Task; };
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious, "relu");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CorruptionSansSource = () => DrDxt.App.Platform.CorruptionDuMagasin.Choisir(
            DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n')));
        vm.LancerReparationCommand.Execute(null);
        console.SetResult();
        await vm.FinDeReparation!;

        vm.LabelReparationTitre.Should().Be("Reparation.Windows.Reinstallation.Titre");
        vm.LancerReparationCommand.Execute(null);
        lances.Should().NotContain(p => p.Arguments.Contains("ResetBase"));
        lances[^1].FileName.Should().Be("ms-settings:recovery");
    }

    [Fact]
    public async Task Une_reparation_sans_source_sur_des_composants_en_service_ouvre_la_reinstallation_de_Microsoft()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var lances = new List<System.Diagnostics.ProcessStartInfo>();
        var console = new TaskCompletionSource();
        vm.LancerConsole = psi => { lances.Add(psi); return psi.FileName.StartsWith("ms-settings") ? null : console.Task; };
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious, "relu");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CorruptionSansSource = () => DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller;
        vm.LancerReparationCommand.Execute(null);
        console.SetResult();
        await vm.FinDeReparation!;

        vm.EtatReparation.Should().Be("Reparation.Windows.Terminee.Reinstaller");
        vm.LabelReparationTitre.Should().Be("Reparation.Windows.Reinstallation.Titre");
        vm.LancerReparationCommand.Execute(null);
        lances[^1].FileName.Should().Be("ms-settings:recovery", "relancer la même réparation ne changerait rien");
        vm.EtatReparation.Should().Be("Reparation.Windows.Reinstallation.Ouverte");
    }

    private const string CbsSansSource = """
        2026-10-04 04:59:09, Info  CBS    (p)	CSI Payload Corrupt	(n)			amd64_userexperience-oobe_31bf3856ad364e35_10.0.26100.1591_none_54983d2d7a4908c4\OOBE\a.js
        2026-10-04 04:59:09, Info  CBS    (p)	CSI Payload Corrupt	(n)			amd64_windowssearchengine.resources_31bf3856ad364e35_7.0.26100.1455_fr-fr_ee5c163ff327bb7f\b.dll
        2026-10-04 04:59:10, Info  CBS    Total Detected Corruption:	2
        2026-10-04 04:59:10, Info  CBS    Failed to run Detect and repair. [HRESULT = 0x800f0915 - CBS_E_REPAIR_CONTENT_MISSING]
        """;

    // Une ligne « Corrupt » d'un autre format (fichier en service au hash faux), ou un compte
    // qui ne retombe pas sur le total écrit par CBS, ne doit jamais mener au nettoyage irréversible.
    [Fact]
    public void Une_lecture_incomplete_du_journal_CBS_garde_la_reparation()
    {
        var winsxs = new[]
        {
            "amd64_userexperience-oobe_31bf3856ad364e35_10.0.26100.9278_none_54b61c157a331ee2",
            "amd64_windowssearchengine.resources_31bf3856ad364e35_7.0.26100.9278_fr-fr_0000000000000000",
        };
        var complet = DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n'));
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(complet)
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller, "témoin : lecture complète, source introuvable");

        var autreFormat = CbsSansSource.Replace("Total Detected Corruption:\t2",
            "(p)\tCSI Payload File Corrupt\t0x00000000\tsvchost.exe\tamd64_x\tHashes for file member do not match\n"
            + "2026-10-04 04:59:10, Info  CBS    Total Detected Corruption:\t3");
        var lu = DrDxt.App.Platform.CorruptionDuMagasin.Lire(autreFormat.Split('\n'));
        lu.Complete.Should().BeFalse();
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu).Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reparer);

        var compteFaux = CbsSansSource.Replace("Total Detected Corruption:\t2", "Total Detected Corruption:\t5");
        DrDxt.App.Platform.CorruptionDuMagasin.Lire(compteFaux.Split('\n')).Complete.Should().BeFalse("2 lues sur 5 annoncées");
    }

    [Fact]
    public void Le_journal_CBS_nomme_les_composants_abimes_et_la_source_manquante()
    {
        var lu = DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n'));
        lu.SourceIntrouvable.Should().BeTrue();
        lu.Composants.Should().HaveCount(2);

        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu)
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller);
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu with { SourceIntrouvable = false })
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reparer, "sans échec lu, la réparation reste le geste");
    }
}
