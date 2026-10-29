using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Source unique du registre des leviers et des constats du scan : <see cref="ScoringRules"/> et le scorer consomment la
/// même évaluation, le gain d'un constat (non affiché) == les points retirés.
///  - Les magnitudes sont des estimations d'expert (paliers Major/Minor), pas des pourcentages mesurés ; la mesure vit
///    dans le bloc A/B PresentMon.
///  - Les défauts neutres de Windows (HAGS coupé, Équilibré, mode Jeu coupé, VRR sans écran VRR) sont des occasions à
///    impact 0.
///  - Chaque pénalité dépend du matériel ou du symptôme qui rend le gain réel.
///  - Unknown ne retire rien mais compte dans Coverage.
/// Indépendant du fabricant : seuls les contrats <see cref="HardwareInventory"/> / <see cref="SettingsState"/>.
/// </summary>
internal static class LeverCatalog
{
    // Groupes d'affichage (paniers de sous-score) : des regroupements d'affichage, pas des poids.
    public const string GroupPerformance = "Performance";
    public const string GroupLatency = "Latency";
    public const string GroupStability = "Stability";

    // Clés de source (audit C1) : le palier vient d'une fiche north-star ; le chiffre exact est une estimation, jamais
    // présentée comme mesurée.
    private const string SrcRam = "Source.RamAll";
    private const string SrcCpu = "Source.CpuAll";
    private const string SrcGpu = "Source.GpuVendors";
    private const string SrcOsLatency = "Source.OsLatency";

    // Planchers de fraîcheur du pilote : un marqueur de corruption ou une branche de tête sous le
    // plancher du fabricant compte comme périmé.
    private const int NvidiaBranchFloor = 500; // NVIDIA "551.86" → branch 551
    private const int OtherBranchFloor = 20;   // AMD/Intel « 24.x »/« 31.0.x » → premier groupe minuscule = ancien

    /// <summary>
    /// Un levier évalué : ce que consomment le scorer et la table des constats, pour qu'ils ne divergent jamais.
    /// </summary>
    public sealed record LeverResult(
        string LeverId,
        string Group,
        LeverState State,
        int Penalty,            // points retirés : non nul seulement si State == Suboptimal
        string TitleKey,        // clé de traduction représentative qui nomme le levier
        string SourceKey,
        Finding? Finding,       // constat affiché (null = rien à montrer)
        bool CountsForCoverage, // vrai pour les vrais leviers notés (exclut les occasions neutres)
        // Non nul quand la mesure dit que ce levier ne rendra pas d'images ici : constat et penalite restent, la promesse
        // tombe. Motif montrable tel quel (comme FrameCapState.Reason).
        string? GainSuppressedReason = null)
    {
        /// <summary>Vrai quand la mesure interdit de vendre ce levier comme des images en plus.</summary>
        public bool GainSuppressed => GainSuppressedReason is not null;
    }

    /// <param name="measured">Le verdict de goulot mesure cette session, pour ne pas proposer les memes promesses a une
    /// machine GPU-bound a 97 % et a une machine etranglee par la validation memoire. null = pas de mesure : rien n'est
    /// supprime.</param>
    public static IReadOnlyList<LeverResult> Evaluate(
        HardwareInventory hw, SettingsState state, BottleneckVerdict? measured = null)
    {
        var desktop = hw.Chassis is ChassisType.Desktop;
        // Le ReBAR se lit sur la carte dédiée : une puce intégrée listée en premier n'a pas de BAR à agrandir.
        var primaryGpu = CartesGraphiques.CarteDediee(hw);
        // Symptôme de parcage = condition du seul routage X3D (la politique de parcage est identique entre Equilibre et
        // Haute performance, SanePowerPlanTweak). Des coeurs parques au repos sont la norme. Tri-etat : true = mal parque
        // (la règle de fixit.x3d_ccd : un coeur du CCD à cache parqué), false = seul le CCD de fréquence parqué (routage
        // sain), null = pas lu (hors jeu) ; null n'arme rien et ne compte pas comme sain. Rien de parqué reste « non
        // confirmé », le levier le propose sous garde.
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
            EvaluateHagsOpportunity(state), // neutre (C2) : affiché si coupé, jamais noté
            EvaluateDisplayRefresh(state),
            EvaluatePagefile(state),
            EvaluateMmcss(state),
            EvaluateSysMain(state),
        };

        // La mesure ne change que ce qu'on raconte, penalite comprise : le registre decrit la configuration, il ne bouge
        // pas selon qu'un jeu tournait au scan.
        return levers
            .Select(l => l with
            {
                GainSuppressedReason = BottleneckLeverPolicy.SuppressionReason(l.LeverId, measured),
            })
            .ToList();
    }

    // --- XMP / EXPO (Performance, Major 15) : le seul vrai gros levier de RAM. ----------------
    // Condition : PC de bureau seulement ; le firmware des portables et consoles portables verrouille le profil mémoire (NotApplicable).
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

    // --- Pilote GPU très ancien / corrompu (Performance, Major 15). ----
    // Logiciel seulement (DDU guidé) : s'applique sur tout châssis avec un GPU. Un faux positif ne fait que proposer une
    // réinstallation guidée. Seule une branche de fabricant se juge : WMI rend une version WDDM à quatre groupes, que
    // seul NVIDIA remplace par sa branche (NVML) ; sur AMD et Intel, le levier ne se déclenche donc pas.
    private static LeverResult EvaluateDriver(HardwareInventory hw)
    {
        const int magnitude = 15;
        const string key = "Finding.Gpu.DriverStale";
        if (hw.Gpus.Count == 0)
            return NotApplicable("gpu-driver", GroupPerformance, key, SrcGpu);
        // Un pilote lu et périmé se dit d'abord : une autre carte non lue (l'iGPU d'un portable) ne le cache pas.
        if (hw.Gpus.Where(g => !PiloteNonLu(g)).Any(IsDriverStaleOrCorrupt))
            return new("gpu-driver", GroupPerformance, LeverState.Suboptimal, magnitude, key, SrcGpu,
                new Finding("Gpu", Severity.Major, key, magnitude), true);
        // « Unknown » = GpuDetector n'a pas lu la version (WMI en échec, ligne ambiguë) : non lu, jamais « périmé ».
        if (hw.Gpus.Any(PiloteNonLu))
            return new("gpu-driver", GroupPerformance, LeverState.Unknown, 0, key, SrcGpu, null, true);
        return new("gpu-driver", GroupPerformance, LeverState.Optimal, 0, key, SrcGpu, null, true);
    }

    // --- Routage X3D multi-CCD (Performance, Major 12) ------------------------------
    // X3D à plus d'un CCD seulement ; pénalité sur une erreur confirmée par le symptôme de parcage. Sans lui (parcage lu
    // jeu ouvert seulement, AMD3DVCacheSvc non lu) : Unknown (0) et un constat Info.
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

    // --- ReBAR coupé (Performance, Minor 3) : guidage BIOS. --------------------------------------
    // PC de bureau + un GPU. Lu par NVML (NvmlReBarProvider) chez NVIDIA, ADLX (SAM) chez AMD. Lecture manquante : un
    // constat de guidage sur NVIDIA (le levier dominant reste visible), « illisible » ailleurs ; aucun point.
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

    // --- VRR coupé (Latence, Minor 5) -------------------------------------------------
    // Un déficit seulement sur un écran VRR confirmé ; sinon NotApplicable (0), occasion neutre s'il est coupé.
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
            // Pas noté. VRR coupé : occasion, sauf si le pilote a lu l'ecran non capable (NVAPI rend « VRR impossible »
            // sur les deux ecrans de la machine de reference : conseiller de l'activer serait un faux besoin).
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

    // --- Pas de `power-plan` au registre. -------------
    // « Equilibre + coeurs parques » n'est pas un probleme prouve : le parking est identique entre les
    // plans (`powercfg /qh`). La piste CPU vit dans le candidat `fixit.power_plan`, arme par le goulot
    // mesure ; la mesure ne bouge jamais le registre de configuration.

    // --- HAGS coupé : occasion neutre (audit C2), jamais un déficit. ----------------------------
    // Coupé par défaut sur la plupart des installations, gain « non garanti » (Microsoft) : ni points ni couverture.
    private static LeverResult EvaluateHagsOpportunity(SettingsState state)
    {
        const string key = "Finding.Latency.HagsOff";
        var opportunity = state.Hags == TriState.Off
            ? new Finding("Latency", Severity.Info, key, null)
            : null;
        return new("hags", GroupLatency, LeverState.NotApplicable, 0, key, SrcOsLatency, opportunity, false);
    }

    // --- L'ecran tourne sous sa frequence maximale (Performance, Major 15) -----------------------
    // Sans materiel ni risque, un menu Windows : un panneau 200 Hz pilote a 143 Hz jette 28 % des images deja rendues
    // (machine de reference, 12 et 15/08/2026, pendant qu'un scan la declarait saine). Le rang de XMP, et le seul gain
    // arithmetique plutot qu'estime. Unknown ne deduit rien (regle 3 de l'heuristique) : « modes non enumeres » n'est
    // pas « deja au maximum ».
    private static LeverResult EvaluateDisplayRefresh(SettingsState state)
    {
        const int magnitude = 15;
        const string key = "Finding.Display.RefreshBelowMax";

        if (state.DisplayRefresh is not { } headroom)
            // Aucune sonde d'affichage : non evalue, donc visible comme non couvert.
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

    // ═══ Les degats deja infliges par un « optimiseur » precedent ═══════════════════════
    // Trois regressions, trois traitements : seul un risque source est penalise. Des reparations, aucune promesse
    // d'images.

    // --- Pagefile desactive (Stabilite, Minor 5) : regression penalisee. -----------------------
    // A la limite de validation, le systeme et les processus peuvent geler, planter ou mal fonctionner : des plantages,
    // pas des images. Source : https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
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

    // --- MMCSS coupe (Latence, Minor 3) : regression penalisee. -------------------------------
    // `SystemResponsiveness = 100` coupe MMCSS, le mecanisme dont depend le service audio, sans gain
    // en jeu prouve. Le 0 recopie partout est ramene a 20 par Windows : sans effet, jamais penalise.
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

    // --- SysMain desactive : regression signalee, jamais penalisee. ---------------------------
    // Le geste « optimiseur » le plus courant, mais gain non prouve sur une install saine (note memoire du projet, non
    // publiee) et effet negligeable sur SSD (Microsoft : https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent).
    // Le penaliser fabriquerait un deficit (audit C2). Le constat dit que la machine a ete « optimisee » par un autre.
    private static LeverResult EvaluateSysMain(SettingsState state)
    {
        const string key = "Finding.Memory.SysMainOff";
        var regression = state.SysMain == TriState.Off
            ? new Finding("Memory", Severity.Info, key, null)
            : null;
        // Non note, donc hors couverture (comme HAGS).
        return new("sysmain", GroupStability, LeverState.NotApplicable, 0, key, SrcRam, regression, false);
    }

    private static LeverResult NotApplicable(string id, string group, string titleKey, string src) =>
        new(id, group, LeverState.NotApplicable, 0, titleKey, src, Finding: null, CountsForCoverage: false);

    /// <summary>Version absente, vide (WmiRow rend "" pour une propriété vide) ou « Unknown » (GpuDetector) : non lu.</summary>
    private static bool PiloteNonLu(GpuInfo gpu) =>
        string.IsNullOrWhiteSpace(gpu.DriverVersion)
        || string.Equals(gpu.DriverVersion.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

    // Non lue : jamais périmée ; non numérique : corruption ; branche de tête sous le plancher du fabricant : ancienne.
    private static bool IsDriverStaleOrCorrupt(GpuInfo gpu)
    {
        var version = gpu.DriverVersion;
        if (PiloteNonLu(gpu))
            return false;
        // Forme WDDM à 4 groupes (« 32.0.16.1088 », WMI) : la tête est la version WDDM, aucun signal de fraîcheur.
        if (IsWddmQuad(version))
            return false;
        var branch = LeadingBranch(version);
        if (branch is null)
            return true;
        var nvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        return branch.Value < (nvidia ? NvidiaBranchFloor : OtherBranchFloor);
    }

    // « a.b.c.d » à quatre groupes entiers = la version de pilote WDDM rapportée par WMI.
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
