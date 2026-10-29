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
