using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Source unique du registre des leviers et des constats du scan.
///
/// Sans ce catalogue, <see cref="ScoringRules"/> (constats + EstimatedGainPct) et le scorer
/// porteraient deux jeux de chiffres divergents (XMP : constat 12 % contre score −18). Ils
/// consomment la même évaluation ici : le gain annoncé par un constat == les points retirés.
///
/// Doctrine (corrigée par l'audit adverse du projet, non publié) :
///  - Les magnitudes sont des estimations d'expert étalonnées (paliers ordinaux Major/Minor), pas
///    des pourcentages mesurés ou « sourcés » (audit C1). La vraie mesure ne vit que dans le bloc A/B PresentMon.
///  - On ne pénalise pas les défauts neutres de Windows (HAGS coupé, Équilibré, mode Jeu coupé, VRR
///    coupé sur un écran sans VRR) : ce sont des occasions à impact 0, jamais un déficit inventé (audit C2).
///  - Chaque pénalité dépend du matériel ou du symptôme qui rend le gain réel.
///  - Unknown (illisible) ne retire rien mais compte dans Coverage. Le ReBAR NVIDIA se lit par
///    NVML (NvmlReBarProvider) et celui d'AMD par ADLX : On/Off sont réels ; quand une lecture manque,
///    on pousse quand même un constat de guidage sur NVIDIA pour que le levier dominant reste visible.
///
/// Indépendant du fabricant : marche pour AMD, Intel et NVIDIA à partir des seuls contrats publics
/// <see cref="HardwareInventory"/> / <see cref="SettingsState"/>.
/// </summary>
internal static class LeverCatalog
{
    // Groupes d'affichage (paniers de sous-score) : des regroupements d'affichage, pas des poids.
    public const string GroupPerformance = "Performance";
    public const string GroupLatency = "Latency";
    public const string GroupStability = "Stability";

    // Clés de source (audit C1) : le palier ordinal vient d'une fiche north-star ; le chiffre exact
    // est une estimation étalonnée, montrée pour l'audit, jamais présentée comme mesurée.
    private const string SrcRam = "Source.RamAll";
    private const string SrcCpu = "Source.CpuAll";
    private const string SrcGpu = "Source.GpuVendors";
    private const string SrcOsLatency = "Source.OsLatency";

    // Planchers de fraîcheur du pilote : un marqueur de corruption ou une branche de tête sous le
    // plancher du fabricant compte comme périmé.
    private const int NvidiaBranchFloor = 500; // NVIDIA "551.86" → branch 551
    private const int OtherBranchFloor = 20;   // AMD/Intel « 24.x »/« 31.0.x » → premier groupe minuscule = ancien

    /// <summary>
    /// Un levier évalué : tout ce que consomment à la fois le scorer (pénalité / registre /
    /// couverture) et la table des constats, pour que les deux ne divergent jamais.
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
        // Non nul quand la mesure dit que ce levier ne rendra pas d'images sur cette
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
    /// Avant ce parametre, LeverCatalog ne recevait jamais de mesure. Le produit mesurait
    /// d'un cote et recommandait de l'autre, donc il proposait les memes leviers a une machine
    /// GPU-bound a 97 % et a une machine etranglee par la validation memoire.
    /// </param>
    public static IReadOnlyList<LeverResult> Evaluate(
        HardwareInventory hw, SettingsState state, BottleneckVerdict? measured = null)
    {
        var desktop = hw.Chassis is ChassisType.Desktop;
        var primaryGpu = hw.Gpus.Count > 0 ? hw.Gpus[0] : null;
        // Symptôme de parcage des cœurs = condition du seul routage X3D. Le levier du plan d'alimentation ne
        // s'en sert pas (SanePowerPlanTweak : la politique de parcage est identique entre Equilibre et Haute
        // performance). Au repos, des coeurs parques sont la norme : ce signal n'est pas un defaut a lui seul.
        // Tri-etat : true = coeurs parques, false = lu et aucun, null = pas lu. Le
        // troisieme cas ne doit ni armer un levier, ni compter comme une machine saine.
        // « des coeurs parqués » ne suffit pas ; seul le CCD de fréquence parqué, c'est le
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
            EvaluateHagsOpportunity(state), // neutre (C2) : affiché si coupé, jamais noté
            EvaluateDisplayRefresh(state),
            EvaluatePagefile(state),
            EvaluateMmcss(state),
            EvaluateSysMain(state),
        };

        // La mesure ne change que ce qu'on raconte. Chaque champ du levier est repris tel
        // quel, penalite comprise : le registre decrit la configuration, et le faire dependre
        // d'une mesure ferait bouger le registre d'une machine selon qu'un jeu tournait ou non au
        // moment du scan.
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
    // Indépendant du fabricant (« levier no.1 » sur Arc, gros gains DX9/DX11). Logiciel seulement (DDU
    // guidé) : s'applique sur tout châssis avec un GPU. Un faux positif ne fait que proposer une réinstallation guidée.
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
    // Ne s'applique qu'à un X3D à plus d'un CCD. Pénalité seulement sur une erreur confirmée. Le seul
    // signal de confirmation lisible est le symptôme de parcage ; sans lui, on ne peut pas confirmer un
    // routage sain (le lecteur de cœurs parqués est un stub et l'état d'AMD3DVCacheSvc n'est pas lu) :
    // on reste Unknown (0) avec un constat Info, jamais de punition pour une machine X3D saine.
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
    // Condition : PC de bureau + un GPU. Le ReBAR NVIDIA se lit par NVML (NvmlReBarProvider) et celui
    // d'AMD par ADLX (SAM) → On/Off sont réels. La branche Unknown est le repli quand la lecture manque :
    // sur NVIDIA on affiche quand même un constat de guidage, pour que le levier dominant reste visible,
    // ailleurs une info honnête « illisible » ; ni l'un ni l'autre ne coûte de points.
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
    // Un déficit seulement si un écran compatible VRR est confirmé. Sans confirmation, on ne peut pas
    // affirmer le gain → NotApplicable pour le score (0), affiché comme occasion neutre s'il est coupé.
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
            // Pas d'écran VRR confirmé → pas noté. Si le VRR est coupé, on le montre comme occasion,
            // sauf si le pilote a lu l'ecran non capable : sur la machine de
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

    // --- Pas de `power-plan` au registre. -------------
    // « Equilibre + coeurs parques » n'est pas un probleme prouve : le parking est identique entre les
    // plans (`powercfg /qh`). La piste CPU vit dans le candidat `fixit.power_plan`, arme par le goulot
    // mesure ; la mesure ne bouge jamais le registre de configuration.

    // --- HAGS coupé : occasion neutre (audit C2), jamais un déficit. ----------------------------
    // HAGS est coupé par défaut sur la plupart des installations et son gain est « non garanti »
    // (Microsoft). Affiché comme occasion s'il est coupé, il ne retire jamais rien et n'entre pas
    // au dénominateur de la couverture.
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
    // Le levier le plus gratuit d'un PC de jeu : il ne demande ni materiel, ni risque, ni
    // compromis de qualite d'image, juste un menu Windows. Et aucun autre levier du catalogue ne
    // le compense. Un panneau 200 Hz pilote a 143 Hz jette 28 % des images que le GPU rend deja.
    //
    // Mesure sur la machine de reference les 12 et 15/08/2026 : exactement ce cas, trois jours
    // durant, pendant qu'un scan la declarait saine.
    //
    // Magnitude 15 = le meme rang que XMP, et c'est deliberé : les deux sont des leviers
    // Performance majeurs, et celui-ci est le seul dont le gain est arithmetique plutot
    // qu'estime : les images sont deja rendues, elles ne sont simplement pas affichees.
    //
    // Unknown ne deduit rien : « je n'ai pas pu enumerer les modes » n'est pas « l'ecran est deja
    // au maximum ». C'est la regle 3 de l'heuristique, et la ligne ou ce levier deviendrait un
    // deficit fabrique sur toute machine dont l'affichage ne se lit pas.
    private static LeverResult EvaluateDisplayRefresh(SettingsState state)
    {
        const int magnitude = 15;
        const string key = "Finding.Display.RefreshBelowMax";

        if (state.DisplayRefresh is not { } headroom)
            // Aucune sonde d'affichage : le levier n'a pas ete evalue. Il compte comme non
            // couvert, ce qui le rend visible dans la couverture au lieu de disparaitre.
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
    //
    // Trois regressions, trois traitements differents, et la difference n'est pas un detail de
    // gout : on ne penalise que ce que la recherche du depot source comme un vrai risque. Traiter
    // les trois pareil serait plus simple et malhonnete dans un sens ou dans l'autre.
    //
    // Aucune des trois ne promet d'images. Ce sont des reparations, pas des gains, et c'est
    // exactement ce que le plan demandait : « les traiter en regressions a reparer, pas en
    // opportunites ».

    // --- Pagefile desactive (Stabilite, Minor 5) : regression penalisee. -----------------------
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
    //
    // Le levier ou il aurait ete facile de se tromper. SysMain desactive est le geste
    // « optimiseur » le plus courant, donc la tentation est de le compter comme un defaut. Mais
    // la note memoire du projet (non publiee) conclut : gain **non prouve** sur une install
    // saine, et sur SSD l'effet (positif comme negatif) est negligeable (Microsoft :
    // https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent).
    //
    // Retirer des points pour ca fabriquerait un deficit sur un reglage dont l'impact est
    // documente comme nul : exactement la faute que l'audit adverse reproche (C2). On le dit,
    // on n'en fait pas un chiffre. Le constat garde toute sa valeur : il apprend a l'utilisateur
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

    /// <summary>Version absente, vide (WmiRow rend "" pour une propriété vide) ou « Unknown » (GpuDetector) : non lu.</summary>
    private static bool PiloteNonLu(GpuInfo gpu) =>
        string.IsNullOrWhiteSpace(gpu.DriverVersion)
        || string.Equals(gpu.DriverVersion.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

    // Prudent : une version non lue n'est jamais périmée (voir PiloteNonLu) ; une version non
    // numérique est un marqueur de corruption ; une branche de tête sous le plancher du fabricant =
    // vraiment ancienne. Tout ce qui se lit au plancher ou au-dessus = récent.
    private static bool IsDriverStaleOrCorrupt(GpuInfo gpu)
    {
        var version = gpu.DriverVersion;
        if (PiloteNonLu(gpu))
            return false;
        // Forme WDDM à 4 groupes (« 32.0.16.1088 ») = ce que rapporte WMI. Son groupe de tête est la
        // version WDDM, pas la branche du fabricant : aucun signal de fraîcheur, jamais un déficit.
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
