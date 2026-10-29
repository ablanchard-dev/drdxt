using DrDxt.Core;

namespace DrDxt.Profiles;

/// <summary>Les profils que DrDXT connaît : la seule source que lit le matcher.
/// Volontairement petit et auditable ; grandit par vagues de machines vérifiées.</summary>
public interface IChipProfileCatalog
{
    /// <summary>Tous les profils, base générique comprise.</summary>
    IReadOnlyList<ChipProfile> All();

    /// <summary>La base sûre qui s'applique partout d'emblée. Jamais null.</summary>
    ChipProfile Generic { get; }

    /// <summary>Le profil d'une famille de puce, ou Fail(TWEAK.NOT_APPLICABLE) si aucun n'est
    /// encore établi pour elle.</summary>
    Result<ChipProfile> ByFamily(ChipFamily family);
}

// ChipProfileCatalog : trois profils de base ; chaque gain annoncé cite un document ou une recherche du dépôt, chaque
// TweakId est un levier du vrai catalogue (ChipProfileProvenanceTests), aucun chiffre inventé. Tout est SafetyLevel.Safe
// (dans les bornes, voies prises en charge, réversibles). Tested = false pour les trois tant qu'un banc matériel ne les
// a pas confirmés.
//   Tweak ids referenced (see DrDxt.Application.Tweaks):
//     fixit.power_plan · fixit.x3d_ccd · latency.vrr_vsync_cap · latency.render_queue_cap · gpu.power_limit
//     fixit.sane_pagefile · latency.prefer_max_perf (06/10/2026 : en-têtes NVAPI,
//     https://github.com/NVIDIA/nvapi, et Microsoft Learn sur le fichier d'échange)
//     fixit.ecran_frequence (07/10/2026 : Microsoft Learn, ChangeDisplaySettingsExW et CDS_TEST)
//     fixit.mode_boost (07/10/2026 : Microsoft Learn, PERFBOOSTMODE et PowerWriteACValueIndex)
//     fixit.wifi_performances (10/10/2026, B3 : Microsoft Learn, Wi-Fi power management for modern standby platforms)
//     fixit.plan_economie (Microsoft Learn, Customize the Windows performance power slider)
//     fixit.pagefile_ssd (Microsoft Learn, Introduction to the page file)
//     fixit.cpu_power_limit (10/10/2026, B1 : TechPowerUp, i9-14900K aux limites de 35 à 253 W ; Intel SDM vol. 4, MSR 0x610)
public sealed class ChipProfileCatalog : IChipProfileCatalog
{
    // Identifiants des leviers du catalogue (miroir de DrDxt.Application.Tweaks ; inclusion vérifiée par test).
    private const string TweakPowerPlan      = "fixit.power_plan";
    private const string TweakX3dCcd         = "fixit.x3d_ccd";
    private const string TweakVrrVsyncCap    = "latency.vrr_vsync_cap";
    private const string TweakRenderQueueCap = "latency.render_queue_cap";
    private const string TweakGpuPowerLimit  = "gpu.power_limit";

    // Sources, telles que lues a l'ecran (carte de la machine, recommandations) : chaque texte dit ce qu'il sait et d'ou ;
    // un chiffre ne reste que s'il est public ou mesure (avec la carte). Sinon, l'A/B mesure chez la personne.
    // Provenance, pour la maintenance (jamais affichee) :
    //   SrcCahierLatency  <- cahier des charges interne
    //   SrcCahierPowerPlan <- cahier des charges interne
    //   SrcX3d            <- Hardware Busters (02/04/2023, 1080p, 7950X3D sans parcage des cœurs = 94,12 % de la
    //                        machine bien réglée, soit ~6 %), seule mesure publique trouvée
    //   SrcNvPowerLimit   <- décision interne sur la carte NVIDIA (limite de puissance seule)
    //   SrcNs1Os          <- cahier des charges interne
    // SrcCahierLatency, SrcCahierPowerPlan et SrcNs1Os ne citent aucune source publique : leurs
    // entrées passent de Sourced à Inferred (garde `RienNEstDitSourceSansSourcePublique`).
    private static string SrcCahierLatency => Docteur.T("CP.SrcCahierLatency");
    private static string SrcCahierPowerPlan => Docteur.T("CP.SrcCahierPowerPlan");
    private static string SrcX3d => Docteur.T("CP.SrcX3d");
    // Pas de chiffre : la mesure TechPowerUp trouvée porte sur les connecteurs d'une RTX 4090 (450 W contre 600 W), pas
    // sur une limite de puissance abaissée.
    private static string SrcNvPowerLimit => Docteur.T("CP.SrcNvPowerLimit");
    private static string SrcNs1Os => Docteur.T("CP.SrcNs1Os");

    // OS/latency catalog tweak ids (mirror DrDxt.Application.Tweaks; verified ⊆ real catalog).
    private const string TweakHags        = "latency.hagsOn";
    private const string TweakGameMode    = "latency.gameModeOn";
    private const string TweakSouris      = "latency.mouse_acceleration_off";
    private const string TweakGameDvr     = "latency.gameDvrBackgroundOff";
    private const string TweakVrrFlip     = "latency.vrrFlipModelOn";

    // Fichier d'échange (profil générique) et état P (profil NVIDIA) : chacun garde son AppliesTo (un état lu, Unknown ne
    // déclenche rien) et son juge. Le fichier d'échange agit au redémarrage et se juge sur les parties (retrait si
    // « moins bien en jeu ») ; l'état P agit à chaud, et la session mesurée l'annule s'il ne gagne rien.
    private const string TweakSanePagefile = "fixit.sane_pagefile";
    private const string TweakPreferMaxPerf = "latency.prefer_max_perf";
    private const string TweakEcranFrequence = "fixit.ecran_frequence";
    private static string SrcEcranFrequence => Docteur.T("CP.SrcEcranFrequence");
    private static string SrcPagefile => Docteur.T("CP.SrcPagefile");
    private static string SrcPreferMaxPerf => Docteur.T("CP.SrcPreferMaxPerf");

    // Microsoft Learn, « Manage device restarts after updates ».
    private const string TweakHeuresActives = "fixit.heures_actives";
    private static string SrcHeuresActives => Docteur.T("CP.SrcHeuresActives");

    private readonly ChipProfile _generic;
    private readonly IReadOnlyList<ChipProfile> _all;

    // Learn.microsoft.com, « Options for perf state engine PerfBoostMode ».
    private const string TweakModeBoost = "fixit.mode_boost";
    private const string TweakWifiPerformances = "fixit.wifi_performances";
    private const string TweakPlanEconomie = "fixit.plan_economie";
    private const string TweakPagefileSsd = "fixit.pagefile_ssd";
    private const string SrcPlanEconomie =
        "https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider";
    private const string SrcWifiPerformances =
        "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/wi-fi-power-management-for-modern-standby-platforms";
    private const string TweakCpuPowerLimit = "fixit.cpu_power_limit";
    private const string TweakCpuPowerCap = "fixit.cpu_power_cap";
    // Intel SDM vol. 4, MSR_PKG_POWER_LIMIT (0x610) : PL1 = la limite que le processeur tient dans la durée.
    private static string SrcCpuPowerCap => Docteur.T("CP.SrcCpuPowerCap");
    private const string SrcCpuPowerLimit =
        "https://www.techpowerup.com/review/intel-core-i9-14900k-raptor-lake-tested-at-power-limits-down-to-35-w/";
    private static string SrcModeBoost => Docteur.T("CP.SrcModeBoost");

    public ChipProfileCatalog()
    {
        _generic = BuildGeneric();
        _all = new[] { _generic, BuildAmdX3d(), BuildNvidiaOptionA() };
    }

    public IReadOnlyList<ChipProfile> All() => _all;

    public ChipProfile Generic => _generic;

    public Result<ChipProfile> ByFamily(ChipFamily family)
    {
        foreach (var p in _all)
        {
            if (p.Family == family)
                return Result<ChipProfile>.Ok(p);
        }
        return Result<ChipProfile>.Fail(ErrorCodes.TweakNotApplicable,
            $"No curated profile for chip family '{family}'.");
    }

    // --- Generic safe baseline: cross-vendor, no OC, applies everywhere. ---
    private static ChipProfile BuildGeneric() => new(
        Id: "profile.generic.safe",
        NameKey: "profile.generic.safe.name",
        Family: ChipFamily.GenericSafe,
        Tested: false,
        Summary: "Cross-vendor safe baseline: sane power plan + low-latency display config. " +
                 "No overclock, no vendor SDK risk; helps mainly a misconfigured machine.",
        Selections: new[]
        {
            new ProfileTweakSelection(
                TweakPowerPlan, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain01"),
                    SrcCahierPowerPlan, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakPlanEconomie, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain19"),
                    SrcPlanEconomie, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakWifiPerformances, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain02"),
                    SrcWifiPerformances, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakCpuPowerLimit, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain03"),
                    SrcCpuPowerLimit, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakCpuPowerCap, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain04"),
                    SrcCpuPowerCap, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakModeBoost, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain05"),
                    SrcModeBoost, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakSanePagefile, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain06"),
                    SrcPagefile, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakPagefileSsd, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain20"),
                    SrcPagefile, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakHeuresActives, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain07"),
                    SrcHeuresActives, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakEcranFrequence, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain08"),
                    SrcEcranFrequence, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakVrrVsyncCap, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain09"),
                    SrcCahierLatency, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakRenderQueueCap, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain10"),
                    SrcCahierLatency, GainConfidence.Inferred, SafetyLevel.Safe)),

            // Windows et latence, tout constructeur, sur accord du joueur ; l'écart est prouvé par l'A/B, jamais promis.
            // Redémarrage, réversibles, chacun derrière son AppliesTo (HAGS seulement si lisible et éteint).
            new ProfileTweakSelection(
                TweakHags, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain11"),
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakSouris, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain12"),
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGameMode, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain13"),
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGameDvr, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain14"),
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakVrrFlip, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain15"),
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
        });

    // --- AMD X3D à deux CCD : le gain de réparation le plus net. ---
    private static ChipProfile BuildAmdX3d() => new(
        Id: "profile.amd.x3d.dual_ccd",
        NameKey: "profile.amd.x3d.dual_ccd.name",
        Family: ChipFamily.AmdX3dDualCcd,
        Tested: false,
        Summary: "Dual-CCD AMD X3D (7950X3D/7900X3D/9950X3D/9900X3D): repair game routing to the " +
                 "V-Cache CCD via the supported software route. The single biggest real-world gain " +
                 "when misconfigured; no gain if already healthy.",
        Selections: new[]
        {
            new ProfileTweakSelection(
                TweakX3dCcd, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain16"),
                    SrcX3d, GainConfidence.Sourced, SafetyLevel.Safe)),
        });

    // --- NVIDIA : l'état P « performances maximales » au profil du jeu et la limite de puissance. ---
    private static ChipProfile BuildNvidiaOptionA() => new(
        Id: "profile.nvidia.rtx.option_a",
        NameKey: "profile.nvidia.rtx.option_a.name",
        Family: ChipFamily.NvidiaOptionA,
        Tested: false,
        Summary: "Desktop GeForce: prefer maximum performance for the game + Option-A efficiency (power-limit " +
                 "trim). NVAPI-stable, in-bounds, reversible; the felt gain is latency, the tuning is " +
                 "cooler/steadier rather than big FPS.",
        Selections: new[]
        {
            new ProfileTweakSelection(
                TweakPreferMaxPerf, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    Docteur.T("CP.Gain17"),
                    SrcPreferMaxPerf, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGpuPowerLimit, ProfileIntent.GpuTune,
                new[] { new ProfileParameter("PowerLimitPct", 90, "%") },
                new GainEvidence(
                    Docteur.T("CP.Gain18"),
                    SrcNvPowerLimit, GainConfidence.Inferred, SafetyLevel.Safe)),
        });
}
