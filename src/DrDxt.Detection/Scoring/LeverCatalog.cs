using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Single source of truth for the /100 deduction ledger AND the scan findings (plan §2.2, §6.1).
///
/// Before this catalog, <see cref="ScoringRules"/> (findings + EstimatedGainPct) and the scorer
/// held two divergent sets of numbers (XMP: finding 12% vs score −18). They now consume the same
/// evaluation here, so a finding's announced gain == the points the score removed.
///
/// Doctrine (corrected by the project's adversarial audit, unpublished):
///  - The magnitudes are calibrated expert estimates (ordinal Major/Minor tiers), not measured or
///    "sourced" percentages (audit C1). Real measurement lives only in the A/B PresentMon block.
///  - We do not penalize neutral Windows defaults (HAGS off, Balanced, Game Mode off, VRR off on a
///    non-VRR display): they are opportunities at 0 impact, never a fabricated deficit (audit C2).
///  - Every penalty is GATED on the hardware/symptom that makes the gain real (audit H2, M5, M12).
///  - Unknown (unreadable) deducts nothing but is tracked in Coverage. NVIDIA ReBAR is read via
///    NVML (NvmlReBarProvider) and AMD via ADLX, so On/Off are real; when a read is unavailable we
///    still push a guidance finding on NVIDIA so the dominant lever is not invisible (audit H3).
///
/// Vendor-agnostic: works for AMD, Intel and NVIDIA from the public <see cref="HardwareInventory"/>
/// / <see cref="SettingsState"/> contracts alone.
/// </summary>
internal static class LeverCatalog
{
    // Display groups (sub-score buckets, plan §3) — regroupements d'affichage, not weights.
    public const string GroupPerformance = "Performance";
    public const string GroupLatency = "Latency";
    public const string GroupStability = "Stability";

    // Source keys (audit C1): the ordinal TIER is drawn from a north-star fiche; the exact number
    // is a calibrated estimate, surfaced for audit, never claimed as measured.
    private const string SrcRam = "Source.RamAll";
    private const string SrcCpu = "Source.CpuAll";
    private const string SrcGpu = "Source.GpuVendors";
    private const string SrcOsLatency = "Source.OsLatency";

    // Driver-freshness floors — mirror of DrDxt.Application.Tweaks.FixIt.GpuDriverFreshness
    // (internal there; this scoring assembly cannot reference it). Kept in sync with that heuristic:
    // a corruption marker or a leading branch below the per-vendor floor counts as stale.
    private const int NvidiaBranchFloor = 500; // NVIDIA "551.86" → branch 551
    private const int OtherBranchFloor = 20;   // AMD/Intel "24.x"/"31.0.x" → tiny first group = old

    /// <summary>
    /// One evaluated lever — everything both the scorer (penalty / ledger / coverage) and the
    /// findings table consume, so the two can never diverge again.
    /// </summary>
    public sealed record LeverResult(
        string LeverId,
        string Group,
        LeverState State,
        int Penalty,            // points removed — non-zero only when State == Suboptimal
        string TitleKey,        // representative localization key naming the lever
        string SourceKey,
        Finding? Finding,       // surfaced finding (null = nothing to show)
        bool CountsForCoverage, // true for real scored levers (excludes neutral opportunities)
        // L1.3 — non nul quand la mesure dit que ce levier ne rendra pas d'images sur cette
        // machine. Le constat et la penalite restent : c'est la promesse de gain qui tombe.
        // Motif en clair, montrable tel quel (meme convention que FrameCapState.Reason).
        string? GainSuppressedReason = null)
    {
        /// <summary>Vrai quand la mesure interdit de vendre ce levier comme des images en plus.</summary>
        public bool GainSuppressed => GainSuppressedReason is not null;
    }

    /// <param name="measured">
    /// Le verdict de goulot mesure cette session. null = pas de mesure (scan sans jeu, service
    /// indisponible) : rien n'est supprime. Ne pas savoir n'est pas une raison de taire un
    /// reglage, c'est une raison de ne pas promettre.
    ///
    /// L1.3 : avant ce parametre, LeverCatalog ne recevait jamais de mesure. Le produit mesurait
    /// d'un cote et recommandait de l'autre, donc il proposait les memes leviers a une machine
    /// GPU-bound a 97 % et a une machine etranglee par la validation memoire.
    /// </param>
    public static IReadOnlyList<LeverResult> Evaluate(
        HardwareInventory hw, SettingsState state, BottleneckVerdict? measured = null)
    {
        var desktop = hw.Chassis is ChassisType.Desktop;
        var primaryGpu = hw.Gpus.Count > 0 ? hw.Gpus[0] : null;
        // Core-parking symptom = the gate for X3D routing (M12) only. Le levier du plan d'alimentation ne
        // s'en sert plus (SanePowerPlanTweak : la politique de parcage est identique entre Equilibre et Haute
        // performance). Au repos, des coeurs parques sont la norme : ce signal n'est pas un defaut a lui seul.
        // Tri-etat : true = coeurs parques, false = lu et aucun, null = pas lu. Le
        // troisieme cas ne doit ni armer un levier, ni compter comme une machine saine.
        // AMD-TOUT (09/10/2026) : « des coeurs parqués » ne suffisait pas ; seul le CCD de fréquence parqué, c'est le
        // routage sain. Le docteur suit la règle du levier fixit.x3d_ccd : un coeur du CCD à cache parqué. Rien de parqué
        // reste « non confirmé » ici (au repos, sain et cassé se ressemblent), le levier le propose sous garde.
        bool? parkingSymptom = state.ParkedCoreIndexes is null
            ? null
            : state.ParkedCoreIndexes.Count == 0 ? false : DrDxt.Application.Tweaks.FixIt.X3dParkingHeuristic.LooksMisparked(hw.Cpu, state);
        var routageSain = state.ParkedCoreIndexes is { Count: > 0 } && parkingSymptom == false;

        var levers = new List<LeverResult>
        {
            EvaluateXmp(state, desktop),
            EvaluateDriver(hw),
            EvaluateX3d(hw, parkingSymptom, routageSain),
            EvaluateRebar(primaryGpu, desktop),
            EvaluateVrr(state),
            EvaluateHagsOpportunity(state), // neutral (C2): surfaced if off, never scored
            EvaluateDisplayRefresh(state),
            EvaluatePagefile(state),
            EvaluateMmcss(state),
            EvaluateSysMain(state),
        };

        // L1.3 — la mesure ne change que ce qu'on raconte. Chaque champ du levier est repris tel
        // quel, penalite comprise : le /100 reste un score de CONFIGURATION, et le faire dependre
        // d'une mesure ferait bouger le score d'une machine selon qu'un jeu tournait ou non au
        // moment du scan.
        return levers
            .Select(l => l with
            {
                GainSuppressedReason = BottleneckLeverPolicy.SuppressionReason(l.LeverId, measured),
            })
            .ToList();
    }

    // --- XMP / EXPO (Performance, Major 15) — the only genuinely large RAM lever. ----------------
    // Gating: desktop only — laptop/handheld firmware locks the memory profile (NotApplicable).
    private static LeverResult EvaluateXmp(SettingsState state, bool desktop)
    {
        const int magnitude = 15;
        const string key = "Finding.Memory.XmpOff";
        if (!desktop)
            return NotApplicable("xmp", GroupPerformance, key, SrcRam);
        return state.XmpExpo switch
        {
            TriState.Off => new("xmp", GroupPerformance, LeverState.Suboptimal, magnitude, key, SrcRam,
                new Finding("Memory", Severity.Major, key, magnitude), true),
            TriState.On => new("xmp", GroupPerformance, LeverState.Optimal, 0, key, SrcRam, null, true),
            _ => new("xmp", GroupPerformance, LeverState.Unknown, 0, key, SrcRam, null, true),
        };
    }

    // --- GPU driver very old / corrupt (Performance, Major 15) — was INVISIBLE to the score. ----
    // Vendor-agnostic ("levier no.1" on Arc, big DX9/DX11 gains). Software-only (guided DDU), so it
    // applies on any chassis with a GPU. A false positive only PROMPTS a guided reinstall.
    private static LeverResult EvaluateDriver(HardwareInventory hw)
    {
        const int magnitude = 15;
        const string key = "Finding.Gpu.DriverStale";
        if (hw.Gpus.Count == 0)
            return NotApplicable("gpu-driver", GroupPerformance, key, SrcGpu);
        if (hw.Gpus.Any(IsDriverStaleOrCorrupt))
            return new("gpu-driver", GroupPerformance, LeverState.Suboptimal, magnitude, key, SrcGpu,
                new Finding("Gpu", Severity.Major, key, magnitude), true);
        return new("gpu-driver", GroupPerformance, LeverState.Optimal, 0, key, SrcGpu, null, true);
    }

    // --- X3D multi-CCD routing (Performance, Major 12) — audit M12. ------------------------------
    // Applicable only on an X3D part with >1 CCD. Penalize only on a CONFIRMED misconfig. The one
    // confirmation signal we can read is the core-parking symptom; without it we cannot confirm
    // healthy routing (the parked-core reader is stubbed and the AMD3DVCacheSvc state is not read),
    // so we stay Unknown (0) and surface an Info finding — never punish a healthy X3D machine.
    private static LeverResult EvaluateX3d(HardwareInventory hw, bool? parkingSymptom, bool routageSain)
    {
        const int magnitude = 12;
        const string key = "Finding.Scheduling.X3dCcd";
        var applicable = hw.Cpu.IsX3D && hw.Cpu.CcdCount > 1;
        if (!applicable)
            return NotApplicable("x3d-ccd", GroupPerformance, key, SrcCpu);
        if (parkingSymptom == true)
            return new("x3d-ccd", GroupPerformance, LeverState.Suboptimal, magnitude, key, SrcCpu,
                new Finding("Scheduling", Severity.Major, key, magnitude), true);
        if (routageSain)
            return new("x3d-ccd", GroupPerformance, LeverState.Optimal, 0, key, SrcCpu, null, true);
        return new("x3d-ccd", GroupPerformance, LeverState.Unknown, 0, key, SrcCpu,
            new Finding("Scheduling", Severity.Info, "Finding.Scheduling.X3dUnknown", null), true);
    }

    // --- ReBAR off (Performance, Minor 3) — BIOS guidance. --------------------------------------
    // Gating: desktop + a GPU. NVIDIA ReBAR is now read via NVML (NvmlReBarProvider) and AMD via
    // ADLX (SAM) → On/Off are real. The Unknown branch is the fallback when the read is unavailable:
    // on NVIDIA we still surface a guidance finding (audit H3) so the dominant lever isn't invisible,
    // on others an honest "unreadable" info — neither touches the /100.
    private static LeverResult EvaluateRebar(GpuInfo? gpu, bool desktop)
    {
        const int magnitude = 3;
        const string key = "Finding.Gpu.ReBarOff";
        if (!desktop || gpu is null)
            return NotApplicable("rebar", GroupPerformance, key, SrcGpu);
        switch (gpu.ReBar)
        {
            case TriState.Off:
                return new("rebar", GroupPerformance, LeverState.Suboptimal, magnitude, key, SrcGpu,
                    new Finding("Gpu", Severity.Minor, key, magnitude), true);
            case TriState.On:
                return new("rebar", GroupPerformance, LeverState.Optimal, 0, key, SrcGpu, null, true);
            default:
                var nvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                var unknownKey = nvidia ? "Finding.Gpu.ReBarGuidance" : "Finding.Gpu.ReBarUnknown";
                return new("rebar", GroupPerformance, LeverState.Unknown, 0, key, SrcGpu,
                    new Finding("Gpu", Severity.Info, unknownKey, null), true);
        }
    }

    // --- VRR off (Latency, Minor 5) — audit H2. -------------------------------------------------
    // A deficit only when a VRR-capable display is CONFIRMED. Without that confirmation we cannot
    // assert the gain → NotApplicable for the score (0), surfaced as a neutral opportunity if off.
    private static LeverResult EvaluateVrr(SettingsState state)
    {
        const int magnitude = 5;
        const string key = "Finding.Latency.VrrOff";
        var displayCapable = state.VrrCapableDisplay == TriState.On;

        if (state.Vrr == TriState.Unknown)
            return new("vrr", GroupLatency, LeverState.Unknown, 0, key, SrcOsLatency,
                new Finding("Latency", Severity.Info, "Finding.Latency.VrrUnknown", null),
                CountsForCoverage: displayCapable);

        if (!displayCapable)
        {
            // Can't confirm a VRR display → not scored. If VRR is off, show it as an opportunity —
            // Sauf si le pilote a LU l'ecran non capable (L8 suite, 10/09/2026) : sur la machine de
            // reference NVAPI rend « VRR impossible » sur les deux ecrans, et « VRR desactive »
            // aurait conseille d'activer ce qui n'existe pas. Un faux besoin, comme power-plan.
            var opportunity = state.Vrr == TriState.Off && state.VrrCapableDisplay != TriState.Off
                ? new Finding("Latency", Severity.Info, "Finding.Latency.VrrInactiveSupportUnknown", null)
                : null;
            return new("vrr", GroupLatency, LeverState.NotApplicable, 0, key, SrcOsLatency, opportunity, false);
        }

        if (state.Vrr == TriState.Off)
            return new("vrr", GroupLatency, LeverState.Suboptimal, magnitude, key, SrcOsLatency,
                new Finding("Latency", Severity.Minor, key, magnitude), true);
        return new("vrr", GroupLatency, LeverState.Optimal, 0, key, SrcOsLatency, null, true);
    }

    // --- `power-plan` retire du registre le 10/09/2026 (lot 1 « preuve du besoin »). -------------
    // Il comptait « Equilibre + coeurs parques » comme un probleme prouve ; le parking est identique
    // entre les plans (`powercfg /qh`). La piste CPU vit dans le candidat `fixit.power_plan`, arme par
    // le goulot mesure — et la mesure ne bouge jamais le registre de configuration.

    // --- HAGS off — NEUTRAL opportunity (audit C2), never a deficit. ----------------------------
    // HAGS is off by default on most installs and its gain is "non garanti" (Microsoft). It is
    // surfaced as an opportunity if off but never deducts and is not in the coverage denominator.
    private static LeverResult EvaluateHagsOpportunity(SettingsState state)
    {
        const string key = "Finding.Latency.HagsOff";
        var opportunity = state.Hags == TriState.Off
            ? new Finding("Latency", Severity.Info, key, null)
            : null;
        return new("hags", GroupLatency, LeverState.NotApplicable, 0, key, SrcOsLatency, opportunity, false);
    }

    // --- L'ecran tourne sous sa frequence maximale (Performance, Major 15) -----------------------
    //
    // L1.4. Le levier le plus GRATUIT d'un PC de jeu : il ne demande ni materiel, ni risque, ni
    // compromis de qualite d'image -- juste un menu Windows. Et aucun autre levier du catalogue ne
    // le compense. Un panneau 200 Hz pilote a 143 Hz jette 28 % des images que le GPU rend deja.
    //
    // Mesure sur la machine de reference les 12 et 15/08/2026 : exactement ce cas, trois jours
    // durant, pendant qu'un scan la declarait saine. L'heuristique etait ecrite et prouvee depuis
    // ce jour-la, et n'etait appelee par personne (plan du levier, non publie : « LEVIER
    // NON CABLE »).
    //
    // Magnitude 15 = le meme rang que XMP, et c'est deliberé : les deux sont des leviers
    // Performance MAJEURS, et celui-ci est le seul dont le gain est arithmetique plutot
    // qu'estime -- les images sont deja rendues, elles ne sont simplement pas affichees.
    //
    // Unknown ne deduit rien : « je n'ai pas pu enumerer les modes » n'est pas « l'ecran est deja
    // au maximum ». C'est la regle 3 de l'heuristique, et la ligne ou ce levier deviendrait un
    // deficit fabrique sur toute machine dont l'affichage ne se lit pas.
    private static LeverResult EvaluateDisplayRefresh(SettingsState state)
    {
        const int magnitude = 15;
        const string key = "Finding.Display.RefreshBelowMax";

        if (state.DisplayRefresh is not { } headroom)
            // Aucune sonde d'affichage : le levier n'a pas ete evalue. Il compte comme NON
            // COUVERT, ce qui le rend visible dans la couverture au lieu de disparaitre.
            return new("display-refresh", GroupPerformance, LeverState.Unknown, 0, key, SrcOsLatency,
                null, true);

        return headroom.HasHeadroom switch
        {
            TriState.On => new("display-refresh", GroupPerformance, LeverState.Suboptimal, magnitude,
                key, SrcOsLatency,
                new Finding("Display", Severity.Major, key, magnitude), true),
            TriState.Off => new("display-refresh", GroupPerformance, LeverState.Optimal, 0, key,
                SrcOsLatency, null, true),
            _ => new("display-refresh", GroupPerformance, LeverState.Unknown, 0, key, SrcOsLatency,
                new Finding("Display", Severity.Info, "Finding.Display.RefreshUnknown", null), true),
        };
    }

    // ═══ L1.5 — les degats deja infliges par un « OPTIMISEUR » PRECEDENT ═══════════════════════
    //
    // Trois regressions, trois traitements differents, et la difference n'est pas un detail de
    // gout : on ne penalise que ce que la recherche du depot source comme un vrai risque. Traiter
    // les trois pareil serait plus simple et malhonnete dans un sens ou dans l'autre.
    //
    // Aucune des trois ne promet d'images. Ce sont des reparations, pas des gains -- et c'est
    // exactement ce que le plan demandait : « les traiter en regressions a reparer, pas en
    // opportunites ».

    // --- Pagefile desactive (Stabilite, Minor 5) — REGRESSION PENALISEE. -----------------------
    // Microsoft documente le fait : a la limite de validation, le systeme et les processus
    // peuvent geler, planter ou mal fonctionner. C'est un risque de fiabilite reel et source, pas
    // une preference. Groupe Stabilite et jamais Performance : desactiver le pagefile ne coute pas
    // des images, ca coute des plantages.
    // Source : https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
    private static LeverResult EvaluatePagefile(SettingsState state)
    {
        const int magnitude = 5;
        const string key = "Finding.Memory.PagefileOff";

        return state.PagefileConfigured switch
        {
            TriState.Off => new("pagefile", GroupStability, LeverState.Suboptimal, magnitude, key,
                SrcRam, new Finding("Memory", Severity.Minor, key, magnitude), true),
            TriState.On => new("pagefile", GroupStability, LeverState.Optimal, 0, key, SrcRam, null, true),
            _ => new("pagefile", GroupStability, LeverState.Unknown, 0, key, SrcRam, null, true),
        };
    }

    // --- MMCSS sabote (Latence, Minor 3) — REGRESSION PENALISEE. ------------------------------
    // `SystemResponsiveness = 0` est le mythe d'optimisation le plus recopie du web. Il est
    // SOURCE comme regression : MMCSS est le mecanisme dont depend le service audio, le forcer a
    // 0 peut degrader l'audio, et aucun gain en jeu n'est prouve. Windows ecrit 20 : un 0 n'arrive
    // jamais tout seul, quelqu'un l'a ecrit.
    // Source : https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
    private static LeverResult EvaluateMmcss(SettingsState state)
    {
        const int magnitude = 3;
        const string key = "Finding.Latency.MmcssBroken";

        return state.MmcssIntact switch
        {
            TriState.Off => new("mmcss", GroupLatency, LeverState.Suboptimal, magnitude, key,
                SrcOsLatency, new Finding("Latency", Severity.Minor, key, magnitude), true),
            TriState.On => new("mmcss", GroupLatency, LeverState.Optimal, 0, key, SrcOsLatency, null, true),
            _ => new("mmcss", GroupLatency, LeverState.Unknown, 0, key, SrcOsLatency, null, true),
        };
    }

    // --- SysMain desactive — regression signalee, jamais penalisee. ---------------------------
    //
    // Le levier ou il aurait ete facile de se tromper. SysMain desactive est le geste
    // « optimiseur » le plus courant, donc la tentation est de le compter comme un defaut. Mais
    // la note memoire du projet (non publiee) conclut : gain **non prouve** sur une install
    // saine, et sur SSD l'effet -- positif comme negatif -- est negligeable (Microsoft :
    // https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent).
    //
    // Retirer des points pour ca fabriquerait un deficit sur un reglage dont l'impact est
    // documente comme nul : exactement la faute que l'audit adverse reproche (C2). On le dit,
    // on n'en fait pas un chiffre. Le constat garde toute sa valeur -- il apprend a l'utilisateur
    // que sa machine a ete « optimisee » par quelqu'un d'autre.
    private static LeverResult EvaluateSysMain(SettingsState state)
    {
        const string key = "Finding.Memory.SysMainOff";
        var regression = state.SysMain == TriState.Off
            ? new Finding("Memory", Severity.Info, key, null)
            : null;
        // NotApplicable + hors couverture : le levier n'est pas note, donc il ne doit pas non plus
        // peser sur le denominateur d'honnetete du scan (meme traitement que HAGS).
        return new("sysmain", GroupStability, LeverState.NotApplicable, 0, key, SrcRam, regression, false);
    }

    private static LeverResult NotApplicable(string id, string group, string titleKey, string src) =>
        new(id, group, LeverState.NotApplicable, 0, titleKey, src, Finding: null, CountsForCoverage: false);

    // Mirror of GpuDriverFreshness.IsStaleOrCorrupt (DrDxt.Application, internal). Conservative:
    // empty/non-numeric version = corruption marker; a leading branch below the per-vendor floor
    // = genuinely old. Anything parseable at/above the floor = fresh.
    private static bool IsDriverStaleOrCorrupt(GpuInfo gpu)
    {
        var version = gpu.DriverVersion;
        if (string.IsNullOrWhiteSpace(version))
            return true;
        // WDDM 4-group form ("32.0.16.1088") = what WMI reports. Its leading group is the WDDM
        // version, not the vendor branch, so it carries no freshness signal — never a deficit.
        if (IsWddmQuad(version))
            return false;
        var branch = LeadingBranch(version);
        if (branch is null)
            return true;
        var nvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        return branch.Value < (nvidia ? NvidiaBranchFloor : OtherBranchFloor);
    }

    // "a.b.c.d" with four integer groups = the WDDM driver version reported by WMI.
    private static bool IsWddmQuad(string version)
    {
        var parts = version.Trim().Split('.');
        return parts.Length == 4
            && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private static int? LeadingBranch(string version)
    {
        var trimmed = version.Trim();
        var firstDot = trimmed.IndexOf('.');
        var head = firstDot >= 0 ? trimmed[..firstDot] : trimmed;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
