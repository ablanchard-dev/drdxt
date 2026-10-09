using DrDxt.Core;

namespace DrDxt.Profiles;

/// <summary>The set of profiles DrDXT knows about. The single source the matcher reads.
/// Kept tiny + auditable for the foundation; grows by Warzup waves (cahier §10).</summary>
public interface IChipProfileCatalog
{
    /// <summary>Every profile, including the generic baseline.</summary>
    IReadOnlyList<ChipProfile> All();

    /// <summary>The always-applicable safe baseline (cahier §10: "le générique marche
    /// partout d'emblée"). Never null.</summary>
    ChipProfile Generic { get; }

    /// <summary>The profile for a chip family, or Fail(TWEAK.NOT_APPLICABLE) if none is
    /// curated for it yet.</summary>
    Result<ChipProfile> ByFamily(ChipFamily family);
}

// -----------------------------------------------------------------------
// M6 — ChipProfileCatalog: the concrete, SOURCED profiles.
//
// Three foundation profiles, every gain claim cited to an in-repo doc/research file.
// Every TweakId here is an existing M2 catalog tweak (asserted against the real catalog
// by ChipProfileProvenanceTests). NO fabricated numbers (cahier §6/§11). All selections
// are SafetyLevel.Safe (in-bounds, supported routes, reversible). Tested = false for all
// three: grounded but not yet hardware-validated by the team bench — they are flipped to
// true per-wave as the Warzup fleet confirms them (cahier §10/§16).
//
//   Tweak ids referenced (see DrDxt.Application.Tweaks):
//     fixit.power_plan · fixit.x3d_ccd · latency.reflex · latency.vrr_vsync_cap
//     latency.render_queue_cap · gpu.power_limit · gpu.undervolt
//     fixit.sane_pagefile · latency.prefer_max_perf (06/10/2026, L-PROF : en-têtes NVAPI,
//     https://github.com/NVIDIA/nvapi, et Microsoft Learn sur le fichier d'échange)
//     fixit.ecran_frequence (07/10/2026, L-ECRAN : Microsoft Learn, ChangeDisplaySettingsExW et CDS_TEST)
//     fixit.mode_boost (07/10/2026, L-CPU : Microsoft Learn, PERFBOOSTMODE et PowerWriteACValueIndex)
// -----------------------------------------------------------------------
public sealed class ChipProfileCatalog : IChipProfileCatalog
{
    // Catalog tweak ids (mirror DrDxt.Application.Tweaks; verified ⊆ real catalog by test).
    private const string TweakPowerPlan      = "fixit.power_plan";
    private const string TweakX3dCcd         = "fixit.x3d_ccd";
    private const string TweakVrrVsyncCap    = "latency.vrr_vsync_cap";
    private const string TweakRenderQueueCap = "latency.render_queue_cap";
    private const string TweakGpuPowerLimit  = "gpu.power_limit";

    // Sources, telles que la personne les lit a l'ecran (carte de la machine, bloc recommandations).
    //
    // 23/09/2026 — elles citaient nos documents internes (« cahier §3 », « plan north star §2.6 »,
    // « revalidation du 22/06, point R3 », « brief de recherche »). Personne hors du projet ne peut les
    // ouvrir : une source invérifiable n'est pas une source. Chaque texte dit maintenant ce qu'il sait
    // et d'ou il le sait, en clair ; un chiffre ne reste que s'il est PUBLIC (TechPowerUp) ou mesure
    // Par NOUS (et alors il le dit, avec la carte). Les chiffres qui ne venaient que de nos notes sont
    // retires de l'ecran : l'A/B du produit les mesure chez la personne, il ne les promet pas.
    // Provenance interne, pour la maintenance (jamais affichee) :
    //   SrcCahierLatency  <- docs/cahier-des-charges.md §3 et §22
    //   SrcCahierPowerPlan <- CAHIER §4
    //   SrcX3d            <- 08/10/2026 (A6) : la seule mesure publique trouvée, Hardware Busters (02/04/2023, 1080p,
    //                        7950X3D sans parcage des cœurs = 94,12 % de la machine bien réglée, soit ~6 %) ; la note
    //                        interne (+17-21 %, +27 % Warzone) n'a jamais eu de source et n'est plus citée.
    //   SrcNvPowerLimit   <- décision interne de l'option A (limite de puissance seule, Q1) + TechPowerUp 450 W / 600 W
    //   SrcNs1Os          <- plan north star §2.6 + CAHIER §3
    // 08/10/2026 (A6) : SrcCahierLatency, SrcCahierPowerPlan et SrcNs1Os ne citent aucune source publique : leurs
    // entrées passent de Sourced à Inferred (garde `RienNEstDitSourceSansSourcePublique`).
    private const string SrcCahierLatency =
        "Reflex, VRR et file de rendu réduisent la latence système sans coûter d'images par seconde. " +
        "L'ampleur dépend du jeu : DrDXT la mesure sur ta machine plutôt que de l'annoncer.";
    private const string SrcCahierPowerPlan =
        "Le plan Hautes performances évite le micro-saccadé dû au parcage des cœurs du plan Équilibré. " +
        "Gain seulement sur une machine mal réglée, proche de zéro sinon.";
    private const string SrcX3d =
        "Sur un processeur X3D à deux blocs de cœurs (7950X3D, 9950X3D), un jeu placé sur le bloc sans " +
        "cache supplémentaire perd des images : environ 6 % en moyenne en 1080p sur un 7950X3D sans parcage des cœurs (Hardware Busters, avril 2023, https://hwbusters.com/cpu/amd-ryzen-9-7950x3d-core-parking-problem-solution/6/). " +
        "L'écart dépend du jeu : DrDXT le mesure sur ta machine plutôt que de l'annoncer.";
    // 08/10/2026 (V2) : la « mesure publique de TechPowerUp » citée ici portait sur les connecteurs d'alimentation d'une
    // RTX 4090 (450 W contre 600 W), pas sur une limite de puissance abaissée : aucun chiffre n'est plus affiché.
    private const string SrcNvPowerLimit =
        "Abaisser la limite de puissance fait chauffer et consommer la carte moins fort. Aucune mesure publique trouvée " +
        "ne chiffre la perte d'images à 90 % : DrDXT la mesure sur ta machine et annule le réglage s'il en coûte.";
    private const string SrcNs1Os =
        "Réglage de Windows, réversible, sur toutes les cartes. L'effet varie selon le jeu et la machine, " +
        "aucun chiffre n'est promis. L'A/B de DrDXT mesure les images par seconde : un gain de latence " +
        "seul n'y apparaît pas.";

    // NS-1 OS/latency catalog tweak ids (mirror DrDxt.Application.Tweaks; verified ⊆ real catalog).
    private const string TweakHags        = "latency.hagsOn";
    private const string TweakGameMode    = "latency.gameModeOn";
    private const string TweakSouris      = "latency.mouse_acceleration_off";
    private const string TweakGameDvr     = "latency.gameDvrBackgroundOff";
    private const string TweakVrrFlip     = "latency.vrrFlipModelOn";

    // 06/10/2026 (chantier L-PROF, enquête #8) : deux leviers enregistrés, testés, et absents de tout profil,
    // donc jamais proposés par « Optimiser ». Chacun garde son AppliesTo (état LU qui le justifie, Unknown ne déclenche
    // rien) et son juge : le fichier d'échange agit au redémarrage et se juge sur les parties (retrait si « moins bien en
    // jeu ») ; l'état P agit à chaud et passe par la session mesurée en jeu, qui l'annule s'il ne gagne rien.
    private const string TweakSanePagefile = "fixit.sane_pagefile";
    private const string TweakPreferMaxPerf = "latency.prefer_max_perf";
    private const string TweakEcranFrequence = "fixit.ecran_frequence";
    private const string SrcEcranFrequence =
        "Microsoft (learn.microsoft.com, ChangeDisplaySettingsEx) : CDS_TEST demande à Windows si le mode peut être posé sans " +
        "le poser ; CDS_UPDATEREGISTRY range le mode dans le profil de l'utilisateur. Le mode posé est l'un de ceux que Windows " +
        "énumère pour l'écran à sa résolution actuelle (EnumDisplaySettings). Aucun gain d'images n'est annoncé.";
    private const string SrcPagefile =
        "Microsoft (learn.microsoft.com, page d'introduction au fichier d'échange) : la limite de mémoire engagée est la RAM " +
        "plus les fichiers d'échange ; quand elle est atteinte, figeages, plantages et autres défaillances. Le fichier " +
        "d'échange sert aussi aux vidages sur écran bleu. C'est de la fiabilité : aucun gain d'images n'est annoncé.";
    private const string SrcPreferMaxPerf =
        "Réglage « Mode de gestion de l'alimentation » du pilote NVIDIA (PREFERRED_PSTATE, en-tête officiel " +
        "NvApiDriverSettings.h). Aucune source primaire ne chiffre un gain en jeu : il est inconnu, et DrDXT le mesure " +
        "en partie avant de le garder.";

    // 06/10/2026 (chantier L-WU) : Microsoft Learn, « Manage device restarts after updates ».
    private const string TweakHeuresActives = "fixit.heures_actives";
    private const string SrcHeuresActives =
        "Microsoft (learn.microsoft.com, « Manage device restarts after updates ») : les redémarrages automatiques après " +
        "une mise à jour ont lieu hors des heures d'activité ; 18 h au plus. Aucun gain d'images : c'est du confort.";

    private readonly ChipProfile _generic;
    private readonly IReadOnlyList<ChipProfile> _all;

    // 07/10/2026 (L-CPU) : learn.microsoft.com, « Options for perf state engine PerfBoostMode ».
    private const string TweakModeBoost = "fixit.mode_boost";
    private const string SrcModeBoost =
        "Microsoft (learn.microsoft.com, « PERFBOOSTMODE ») : la valeur 0 désactive le boost du processeur, quel que soit " +
        "son mode de pilotage. Son effet en jeu sur un processeur récent n'est pas publié : DrDXT le mesure chez toi.";

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

    // --- Generic safe baseline: cross-vendor, no OC, applies everywhere (cahier §10). ---
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
                    "Plan d'alimentation Hautes performances : retire le micro-saccadé du parcage de " +
                    "cœurs du plan Équilibré. Proche de zéro sur un PC moderne bien réglé ; un vrai " +
                    "correctif seulement quand la machine est mal réglée. Sûr partout.",
                    SrcCahierPowerPlan, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakModeBoost, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Boost du processeur coupé dans le plan d'alimentation, rendu à la valeur par défaut de ce plan. Proposé " +
                    "seulement sur un PC fixe, quand il est lu coupé alors que le défaut du plan l'autorise ; jamais sur un " +
                    "portable, où le couper sert souvent la chaleur et le bruit. Gain inconnu, mesuré en jeu et annulé s'il " +
                    "ne gagne rien. Réversible.",
                    SrcModeBoost, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakSanePagefile, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Fichier d'échange désactivé remis en gestion automatique par Windows : sans lui, la mémoire " +
                    "engagée plafonne à la RAM et un jeu gourmand peut figer ou planter. Fiabilité, pas d'images en " +
                    "plus. Proposé seulement quand il est lu désactivé. Prend effet après un redémarrage. Réversible.",
                    SrcPagefile, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakHeuresActives, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Heures d'activité de Windows Update réglées sur les heures des parties relevées : Windows ne " +
                    "redémarre pas automatiquement le PC pendant ces heures. Confort, pas d'images en plus. Proposé " +
                    "seulement quand des parties relevées sortent des heures actuelles. Windows Update reste allumé. Réversible.",
                    SrcHeuresActives, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakEcranFrequence, ProfileIntent.FixMisconfig,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Écran de jeu remis à la plus haute fréquence que Windows propose à sa résolution actuelle. Proposé " +
                    "seulement quand il est lu en dessous. Windows l'essaie d'abord, DrDXT le relit, et sans confirmation " +
                    "en 15 secondes l'ancien mode revient. Fluidité, pas de chiffre d'images. Réversible.",
                    SrcEcranFrequence, GainConfidence.Sourced, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakVrrVsyncCap, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "VRR + Vsync + plafond d'images sous la fréquence de l'écran : latence système " +
                    "basse et stable, sans perte d'images par seconde. Toutes cartes, sans overclock.",
                    SrcCahierLatency, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakRenderQueueCap, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Plancher de la file de rendu : moins de délai entre ta souris et l'écran. " +
                    "Toutes cartes, sans overclock.",
                    SrcCahierLatency, GainConfidence.Inferred, SafetyLevel.Safe)),

            // NS-1 OS/latency: recommended cross-vendor, applied on the user's opt-in, and the
            // real delta is PROVEN by the engine's A/B (never promised here). All reboot-required
            // + reversible; each AppliesTo-gated downstream (e.g. HAGS only when readable+off).
            new ProfileTweakSelection(
                TweakHags, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Planification GPU accélérée par matériel activée : peut réduire la latence de " +
                    "rendu sur les cartes qui la supportent ; l'effet dépend de la carte, du pilote " +
                    "et du jeu. Prend effet après un redémarrage. Réversible.",
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakSouris, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Accélération du pointeur de Windows coupée : le curseur suit le geste à l'identique, quelle que " +
                    "soit la vitesse ; la visée garde la même échelle. Pas de gain de latence ni d'images : de la " +
                    "justesse. Prend effet à la prochaine ouverture de session. Réversible.",
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGameMode, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Mode Jeu de Windows activé : Windows donne la priorité au jeu en cours ; effet " +
                    "léger, variable selon la machine. Prend effet au prochain lancement du jeu. Réversible.",
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGameDvr, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Enregistrement en arrière-plan de Game DVR désactivé : retire la capture " +
                    "permanente et libère du processeur et de la carte graphique. La capture manuelle " +
                    "reste possible. Réversible.",
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakVrrFlip, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Taux de rafraîchissement variable + optimisations des jeux en fenêtre (modèle " +
                    "flip) activés : moins de latence en fenêtré ou sans bordure. La partie VRR ne joue que si " +
                    "G-Sync ou FreeSync est actif dans le pilote ; le modèle flip joue sans. Auto HDR conservé. Réversible.",
                    SrcNs1Os, GainConfidence.Inferred, SafetyLevel.Safe)),
        });

    // --- AMD dual-CCD X3D: the headline fix-it gain (cahier §5, research R3). ---
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
                    "Répare le mauvais parcage de CCD par le service AMD 3D V-Cache, le CPPC et la " +
                    "Game Bar, sans écriture dans le BIOS. Seulement quand c'est mal réglé ; la seule " +
                    "mesure publique trouvée donne environ 6 % en 1080p (voir la source). Le gain réel " +
                    "est prouvé par la re-mesure A/B sur ta machine, jamais supposé.",
                    SrcX3d, GainConfidence.Sourced, SafetyLevel.Safe)),
        });

    // --- NVIDIA Option A (offset + power-limit) + Reflex (DECISION doc, R2 live). ---
    private static ChipProfile BuildNvidiaOptionA() => new(
        Id: "profile.nvidia.rtx.option_a",
        NameKey: "profile.nvidia.rtx.option_a.name",
        Family: ChipFamily.NvidiaOptionA,
        Tested: false,
        Summary: "Desktop GeForce: Reflex latency floor + Option-A efficiency (power-limit trim + " +
                 "conservative core-clock offset). NVAPI-stable, in-bounds, reversible; the felt gain " +
                 "is latency, the tuning is cooler/steadier rather than big FPS.",
        Selections: new[]
        {
            new ProfileTweakSelection(
                TweakPreferMaxPerf, ProfileIntent.Latency,
                Array.Empty<ProfileParameter>(),
                new GainEvidence(
                    "Performances maximales au profil du jeu seulement (jamais au global) : la carte ne redescend pas " +
                    "en fréquence pendant la partie. Utile seulement si elle redescend ; gain inconnu, mesuré en jeu " +
                    "et annulé s'il ne gagne rien. Proposé seulement quand le réglage est lu et n'est pas déjà sur « Performances maximales ». Réversible.",
                    SrcPreferMaxPerf, GainConfidence.Inferred, SafetyLevel.Safe)),
            new ProfileTweakSelection(
                TweakGpuPowerLimit, ProfileIntent.GpuTune,
                new[] { new ProfileParameter("PowerLimitPct", 90, "%") },
                new GainEvidence(
                    "Limite de puissance abaissée (option A) : la carte chauffe et consomme moins. Son " +
                    "effet sur les images n'a pas de mesure publique : il est mesuré en jeu sur ta machine, " +
                    "et le réglage est annulé s'il en coûte. Efficacité et chaleur, pas un gain d'images.",
                    SrcNvPowerLimit, GainConfidence.Inferred, SafetyLevel.Safe)),
        });
}
