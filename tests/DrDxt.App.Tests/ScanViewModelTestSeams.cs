using System;
using System.Collections.Generic;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Shared default fakes for the apply seams the <see cref="ScanViewModel"/> takes
/// (ISafetyManager / ITweakEngine / IScanProfileConsumer / INavigationService / dashboard
/// factory). Tests that only exercise the SCAN path use these so they don't have to spell
/// out the apply dependencies. The apply behaviour itself is covered by ScanApplyCommandTests
/// / ScanApplyCommitTests with purpose-built consumptions.
/// </summary>
internal static class ScanViewModelTestSeams
{
    // The conservative default GPU targets the App seeds (mirror AddDrDxtApplication).
    public static readonly GpuTuningTarget DefaultTuning = new(0, -100, 0);
    public static readonly GpuPowerLimitTarget DefaultPower = new(0, 90);

    public static ISafetyManager Safety()
    {
        var s = Substitute.For<ISafetyManager>();
        s.EnsureRestorePointAsync().Returns(Result.Ok());
        return s;
    }

    public static ITweakEngine Engine()
    {
        var e = Substitute.For<ITweakEngine>();
        e.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
         .Returns(Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null)));
        return e;
    }

    /// <summary>M6 — a fake dedicated GPU-tuning apply step (default: a no-op Ok report so the
    /// scan/apply tests don't trip the GPU path). The isolated GPU behaviour is covered by
    /// GpuTuningApplyStepTests / ScanApplyGpuCommandTests / GpuTuningIsolationTests.</summary>
    public static IGpuTuningApplyStep GpuTuningStep(Result<ApplyReport>? result = null)
    {
        var s = Substitute.For<IGpuTuningApplyStep>();
        s.ApplyAsync(Arg.Any<ScanProfileConsumption>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
         .Returns(result ?? Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null)));
        return s;
    }

    /// <summary>M6 — a fake profile consumer returning the supplied consumption (default:
    /// an empty no-op so the scan-display tests don't trip the apply path).</summary>
    public static IScanProfileConsumer ProfileConsumer(ScanProfileConsumption? consumption = null)
    {
        var c = Substitute.For<IScanProfileConsumer>();
        c.Consume(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
         .Returns(consumption ?? EmptyConsumption());
        return c;
    }

    public static ScanProfileConsumption EmptyConsumption() =>
        new(new TweakPlan(Array.Empty<string>()), DefaultTuning, DefaultPower,
            Array.Empty<ProfileRecommendationItem>());

    public static ScanProfileConsumption PlanConsumption(string[] planIds, params ProfileRecommendationItem[] items) =>
        new(new TweakPlan(planIds), DefaultTuning, DefaultPower, items);

    /// <summary>Builds a surfaced recommendation line for tests.</summary>
    public static ProfileRecommendationItem Item(
        string tweakId,
        bool tested = false,
        bool inPlan = true,
        ProfileIntent intent = ProfileIntent.FixMisconfig,
        string claim = "claim",
        string source = "source",
        GainConfidence confidence = GainConfidence.Sourced,
        SafetyLevel safety = SafetyLevel.Safe,
        string? note = null,
        bool applicable = true) =>
        new(tweakId, intent, claim, source, confidence, safety, tested, inPlan, note, applicable);

    /// <summary>M3 — a fake BIOS advisor (default: an EMPTY findings list, so the scan-display
    /// tests are unaffected). Pass findings to exercise the surfaced, guidance-only BIOS section
    /// + its fix-misconfig-first ordering (see ScanBiosFindingsTests).</summary>
    public static IBiosAdvisor BiosAdvisor(params BiosFinding[] findings)
    {
        var a = Substitute.For<IBiosAdvisor>();
        a.Advise(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
         .Returns(Result<IReadOnlyList<BiosFinding>>.Ok(findings));
        return a;
    }

    /// <summary>A BIOS advisor whose Advise FAILS — for the best-effort fail-clean path.</summary>
    public static IBiosAdvisor BiosAdvisorFailing()
    {
        var a = Substitute.For<IBiosAdvisor>();
        a.Advise(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
         .Returns(Result<IReadOnlyList<BiosFinding>>.Fail(ErrorCodes.BiosInputInvalid, "boom"));
        return a;
    }

    /// <summary>M3 — a fake guide generator. Default: an Ok PersonalizedGuide with EMPTY steps,
    /// so the scan-display tests leave ScanViewModel.BiosGuide null (a recovery-only guide is
    /// nothing to guide). Pass a guide to surface an actionable one (see ScanBiosGuideTests).</summary>
    public static IGuideGenerator GuideGenerator(PersonalizedGuide? guide = null)
    {
        var g = Substitute.For<IGuideGenerator>();
        g.Generate(Arg.Any<HardwareInventory>(), Arg.Any<IReadOnlyList<BiosFinding>>())
         .Returns(Result<PersonalizedGuide>.Ok(guide ?? Guide()));
        return g;
    }

    /// <summary>A guide generator whose Generate FAILS — for the best-effort fail-clean path.</summary>
    public static IGuideGenerator GuideGeneratorFailing()
    {
        var g = Substitute.For<IGuideGenerator>();
        g.Generate(Arg.Any<HardwareInventory>(), Arg.Any<IReadOnlyList<BiosFinding>>())
         .Returns(Result<PersonalizedGuide>.Fail(ErrorCodes.BiosInputInvalid, "boom"));
        return g;
    }

    /// <summary>Builds a PersonalizedGuide for tests. Default: NO steps (recovery-only → the VM
    /// leaves BiosGuide null). Pass steps to make it actionable; the mandatory recovery ladder is
    /// always present top+bottom.</summary>
    public static PersonalizedGuide Guide(
        GuideConfidence confidence = GuideConfidence.Verified,
        bool csmSuppressed = false,
        string board = "MSI MAG B650 TOMAHAWK WIFI",
        params GuideStep[] steps) =>
        new(BiosVendor.Msi, "Click BIOS 5", board, confidence, BootMode.Uefi,
            Array.Empty<BiosFinding>(), steps,
            new RecoverySection(RecoveryLadder(), RecoveryLadder(), csmSuppressed),
            EnterKey: "Del", SaveKey: "F10");

    /// <summary>Builds an actionable guide carrying explicit enter/save keys (blank keys are
    /// allowed — they exercise the "no instruction" path).</summary>
    public static PersonalizedGuide GuideWithKeys(string enterKey, string saveKey, params GuideStep[] steps) =>
        new(BiosVendor.Msi, "Click BIOS 5", "MSI MAG B650 TOMAHAWK WIFI", GuideConfidence.Verified,
            BootMode.Uefi, Array.Empty<BiosFinding>(),
            steps.Length > 0 ? steps : new[] { Step() },
            new RecoverySection(RecoveryLadder(), RecoveryLadder(), false),
            enterKey, saveKey);

    /// <summary>A canonical actionable guide step carrying its exact "menu → setting → = value".</summary>
    public static GuideStep Step(
        FirmwareSettingKind setting = FirmwareSettingKind.XmpExpo,
        string instructionKey = "Bios.Guide.Step.Xmp",
        string? exactValue = "Ai Tweaker  →  EXPO  →  = EXPO I",
        int order = 1) =>
        new(order, instructionKey, exactValue, setting, IsRecoveryRelated: false);

    // The mandatory recovery ladder (text-only). Fresh list per call so Top and Bottom are
    // independent instances, mirroring the real GuideGenerator.
    private static IReadOnlyList<GuideStep> RecoveryLadder() =>
        new List<GuideStep>
        {
            new(1, "Bios.Guide.Recovery.NoBootNormal", null, FirmwareSettingKind.BiosVersion, true),
            new(2, "Bios.Guide.Recovery.ClearCmos", null, FirmwareSettingKind.BiosVersion, true),
        };

    public static INavigationService Navigation() => Substitute.For<INavigationService>();

    public static Func<DashboardViewModel> DashboardFactory() => static () => new DashboardViewModel();

    public static IDesiredStateStore DesiredStateStore()
    {
        var s = Substitute.For<IDesiredStateStore>();
        s.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        // Default LoadAsync → empty armed state (nothing armed).
        s.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));
        return s;
    }

    /// <summary>Builds a ScanViewModel with the supplied SCAN fakes and default apply seams.</summary>
    public static ScanViewModel Build(
        IHardwareDetector hd,
        ISettingsStateReader sr,
        IBenchmarkRunner br,
        IScanScorer sc,
        IStorageDetector sd,
        Func<string, string> localize,
        IBiosAdvisor? biosAdvisor = null,
        IGuideGenerator? guideGenerator = null,
        // L0.2 — la couture du voyage dans le BIOS. null par defaut : le VM n'affiche alors
        // aucun bouton « ouvrir le BIOS » et ne prononce aucun verdict de retour, ce qui est
        // exactement l'etat qu'avaient tous les tests avant que la couture existe.
        DrDxt.App.Platform.IFirmwareTrip? firmwareTrip = null,
        // L'audit de sante. null par defaut = l'etat qu'avaient tous les tests avant la couture :
        // aucune section, et aucune phrase affirmant quoi que ce soit sur la machine.
        Func<IReadOnlyList<RepairFinding>>? repairAudit = null,
        // Le journal de sante. null par defaut = aucune section « ce qui a change », comme avant la
        // couture. Un vrai `RepairJournalService` se passe d'ici : c'est ainsi qu'on verifie ce que
        // l'ecran recoit quand le journal du disque est ILLISIBLE.
        Func<IReadOnlyList<RepairFinding>, DrDxt.Infrastructure.Repair.JournalDeSante?>? journalSante = null,
        // 11/09/2026 — ce qui tourne a cote du jeu. null par defaut = AucunVoisin, donc une liste
        // vide et la phrase « rien de notable vu (ou pas lu) » : l'etat d'avant la couture.
        DrDxt.App.Platform.ILecteurDeVoisins? voisins = null,
        // 14/09/2026 — ce que le profil propose. null = une consommation vide, comme avant.
        IScanProfileConsumer? profileConsumer = null,
        // 16/09/2026 — la relance en administrateur. null = etat inconnu, aucun bouton.
        bool? estEleve = null,
        Func<bool>? relancerEnAdministrateur = null,
        // 24/09/2026 — le helper eleve. null = aucune action d'ecriture possible, comme avant.
        IElevatedClient? helper = null,
        Func<string, string, string>? writeReport = null,
        // 25/09/2026 — la reference processeur et l'identite de machine. null = aucune mesure CPU,
        // comme avant la couture.
        Func<string>? machineId = null,
        DrDxt.Measurement.Cpu.CpuBaselineService? cpuBaseline = null,
        DrDxt.App.Platform.IGameLocator? gameLocator = null,
        DrDxt.Measurement.PerformanceJournalStore? performanceJournal = null,
        // 03/10/2026 (P3.1c) — la route vers le serveur de la partie. null = on ne regarde pas, comme avant.
        Func<int, TimeSpan, Task<RepairFinding?>>? mesurerRoute = null,
        Func<TimeSpan, Task<ulong?>>? mesurerMemoireLibre = null,
        Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? verifierPilote = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null,
        Func<HardwareInventory, SettingsState, IReadOnlyList<RepairFinding>, string>? exporterMachine = null) =>
        new(hd, sr, br, sc, sd, Safety(), Engine(), profileConsumer ?? ProfileConsumer(), GpuTuningStep(), Navigation(), DashboardFactory(),
            DesiredStateStore(), new AppliedTweakManifestMapper(), biosAdvisor ?? BiosAdvisor(),
            guideGenerator ?? GuideGenerator(), localize, gameLocator: gameLocator, firmwareTrip: firmwareTrip,
            repairAudit: repairAudit, journalSante: journalSante, voisins: voisins,
            estEleve: estEleve, relancerEnAdministrateur: relancerEnAdministrateur, helper: helper, writeReport: writeReport,
            machineId: machineId, cpuBaseline: cpuBaseline, performanceJournal: performanceJournal, mesurerRoute: mesurerRoute,
            mesurerMemoireLibre: mesurerMemoireLibre, verifierPilote: verifierPilote, journalLeviers: journalLeviers,
            registreGains: registreGains, exporterMachine: exporterMachine);

    /// <summary>Aucun reglage au plan, seulement des leviers GPU (machine NVIDIA de bureau).</summary>
    public static ScanProfileConsumption GpuOnlyConsumption(params string[] gpuIds) =>
        new(new TweakPlan(Array.Empty<string>()), DefaultTuning, DefaultPower,
            Array.Empty<ProfileRecommendationItem>())
        { GpuPlan = new TweakPlan(gpuIds) };
}
