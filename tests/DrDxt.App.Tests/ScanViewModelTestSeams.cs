using System;
using System.Collections.Generic;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Faux par défaut partagés pour les coutures d'application que prend le <see cref="ScanViewModel"/>
/// (ISafetyManager / ITweakEngine / IScanProfileConsumer).
/// Les tests qui n'exercent que le chemin du scan s'en servent pour ne pas avoir à
/// épeler les dépendances d'application. Le comportement d'application lui-même est couvert
/// par ScanApplyCommandTests / ScanApplyCommitTests avec des consommations faites exprès.
/// </summary>
internal static class ScanViewModelTestSeams
{
    // Les cibles GPU prudentes par défaut que l'App pose (miroir d'AddDrDxtApplication).
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

    /// <summary>Fausse étape dédiée d'application du réglage GPU (par défaut : un rapport Ok sans
    /// effet, pour que les tests de scan et d'application ne déclenchent pas le chemin GPU). Le
    /// comportement GPU isolé est couvert par GpuTuningApplyStepTests / ScanApplyGpuCommandTests / GpuTuningIsolationTests.</summary>
    /// <summary>Faux consommateur de profil qui rend la consommation fournie (par défaut :
    /// vide et sans effet, pour que les tests d'affichage du scan ne déclenchent pas le chemin d'application).</summary>
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

    /// <summary>Construit une ligne de recommandation remontée pour les tests.</summary>
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

    /// <summary>Faux conseiller BIOS (par défaut : liste de constats vide, pour que les tests
    /// d'affichage du scan ne bougent pas). Passer des constats pour exercer la section BIOS remontée,
    /// guide seulement, et son ordre mauvaise configuration d'abord (voir ScanBiosFindingsTests).</summary>
    public static IBiosAdvisor BiosAdvisor(params BiosFinding[] findings)
    {
        var a = Substitute.For<IBiosAdvisor>();
        a.Advise(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
         .Returns(Result<IReadOnlyList<BiosFinding>>.Ok(findings));
        return a;
    }

    /// <summary>Conseiller BIOS dont Advise échoue, pour le chemin d'échec propre au mieux.</summary>
    public static IBiosAdvisor BiosAdvisorFailing()
    {
        var a = Substitute.For<IBiosAdvisor>();
        a.Advise(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
         .Returns(Result<IReadOnlyList<BiosFinding>>.Fail(ErrorCodes.BiosInputInvalid, "boom"));
        return a;
    }

    /// <summary>Faux générateur de guide. Par défaut : un PersonalizedGuide Ok sans étape, pour
    /// que les tests d'affichage du scan laissent ScanViewModel.BiosGuide null (un guide de récupération
    /// seule n'a rien à guider). Passer un guide pour en faire remonter un actionnable (voir ScanBiosGuideTests).</summary>
    public static IGuideGenerator GuideGenerator(PersonalizedGuide? guide = null)
    {
        var g = Substitute.For<IGuideGenerator>();
        g.Generate(Arg.Any<HardwareInventory>(), Arg.Any<IReadOnlyList<BiosFinding>>())
         .Returns(Result<PersonalizedGuide>.Ok(guide ?? Guide()));
        return g;
    }

    /// <summary>Générateur de guide dont Generate échoue, pour le chemin d'échec propre au mieux.</summary>
    public static IGuideGenerator GuideGeneratorFailing()
    {
        var g = Substitute.For<IGuideGenerator>();
        g.Generate(Arg.Any<HardwareInventory>(), Arg.Any<IReadOnlyList<BiosFinding>>())
         .Returns(Result<PersonalizedGuide>.Fail(ErrorCodes.BiosInputInvalid, "boom"));
        return g;
    }

    /// <summary>Construit un PersonalizedGuide pour les tests. Par défaut : aucune étape (récupération
    /// seule → le VM laisse BiosGuide null). Passer des étapes pour le rendre actionnable ; l'échelle de
    /// récupération obligatoire est toujours présente en haut et en bas.</summary>
    public static PersonalizedGuide Guide(
        GuideConfidence confidence = GuideConfidence.Verified,
        bool csmSuppressed = false,
        string board = "MSI MAG B650 TOMAHAWK WIFI",
        params GuideStep[] steps) =>
        new(BiosVendor.Msi, "Click BIOS 5", board, confidence, BootMode.Uefi,
            Array.Empty<BiosFinding>(), steps,
            new RecoverySection(RecoveryLadder(), RecoveryLadder(), csmSuppressed),
            EnterKey: "Del", SaveKey: "F10");

    /// <summary>Construit un guide actionnable portant des touches entrer/enregistrer explicites (les
    /// touches vides sont permises : elles exercent le chemin « sans instruction »).</summary>
    public static PersonalizedGuide GuideWithKeys(string enterKey, string saveKey, params GuideStep[] steps) =>
        new(BiosVendor.Msi, "Click BIOS 5", "MSI MAG B650 TOMAHAWK WIFI", GuideConfidence.Verified,
            BootMode.Uefi, Array.Empty<BiosFinding>(),
            steps.Length > 0 ? steps : new[] { Step() },
            new RecoverySection(RecoveryLadder(), RecoveryLadder(), false),
            enterKey, saveKey);

    /// <summary>Étape de guide actionnable canonique portant son « menu → réglage → = valeur » exact.</summary>
    public static GuideStep Step(
        FirmwareSettingKind setting = FirmwareSettingKind.XmpExpo,
        string instructionKey = "Bios.Guide.Step.Xmp",
        string? exactValue = "Ai Tweaker  →  EXPO  →  = EXPO I",
        int order = 1) =>
        new(order, instructionKey, exactValue, setting, IsRecoveryRelated: false);

    // L'échelle de récupération obligatoire (texte seul). Liste neuve à chaque appel pour que Top et
    // Bottom soient des instances indépendantes, comme dans le vrai GuideGenerator.
    private static IReadOnlyList<GuideStep> RecoveryLadder() =>
        new List<GuideStep>
        {
            new(1, "Bios.Guide.Recovery.NoBootNormal", null, FirmwareSettingKind.BiosVersion, true),
            new(2, "Bios.Guide.Recovery.ClearCmos", null, FirmwareSettingKind.BiosVersion, true),
        };

    public static IDesiredStateStore DesiredStateStore()
    {
        var s = Substitute.For<IDesiredStateStore>();
        s.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        // LoadAsync par défaut → état armé vide (rien d'armé).
        s.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));
        return s;
    }

    /// <summary>Construit un ScanViewModel avec les faux de scan fournis et les coutures d'application par défaut.</summary>
    public static ScanViewModel Build(
        IHardwareDetector hd,
        ISettingsStateReader sr,
        IBenchmarkRunner br,
        IScanScorer sc,
        IStorageDetector sd,
        Func<string, string> localize,
        IBiosAdvisor? biosAdvisor = null,
        IGuideGenerator? guideGenerator = null,
        // La couture du voyage dans le BIOS. null par defaut : le VM n'affiche alors
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
        // Ce qui tourne a cote du jeu. null par defaut = AucunVoisin, donc une liste
        // vide et la phrase « rien de notable vu (ou pas lu) » : l'etat d'avant la couture.
        DrDxt.App.Platform.ILecteurDeVoisins? voisins = null,
        // Ce que le profil propose. null = une consommation vide, comme avant.
        IScanProfileConsumer? profileConsumer = null,
        // La relance en administrateur. null = etat inconnu, aucun bouton.
        bool? estEleve = null,
        Func<bool>? relancerEnAdministrateur = null,
        // Le helper eleve. null = aucune action d'ecriture possible, comme avant.
        IElevatedClient? helper = null,
        Func<string, string, string>? writeReport = null,
        // La reference processeur et l'identite de machine. null = aucune mesure CPU,
        // comme avant la couture.
        Func<string>? machineId = null,
        DrDxt.Measurement.Cpu.CpuBaselineService? cpuBaseline = null,
        DrDxt.App.Platform.IGameLocator? gameLocator = null,
        DrDxt.Measurement.PerformanceJournalStore? performanceJournal = null,
        // La route vers le serveur de la partie. null = on ne regarde pas, comme avant.
        Func<int, TimeSpan, Task<RepairFinding?>>? mesurerRoute = null,
        Func<TimeSpan, Task<ulong?>>? mesurerMemoireLibre = null,
        Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? verifierPilote = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null,
        Func<HardwareInventory, SettingsState, IReadOnlyList<RepairFinding>, string>? exporterMachine = null,
        IDesiredStateStore? manifeste = null) =>
        new(hd, sr, br, sc, sd, Safety(), Engine(), profileConsumer ?? ProfileConsumer(), manifeste ?? DesiredStateStore(), new AppliedTweakManifestMapper(), biosAdvisor ?? BiosAdvisor(),
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
