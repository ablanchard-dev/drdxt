using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// « ce qui te coûte des images », et son inverse : ce qu'il ne faut pas te vendre parce
/// que ça ne changera rien chez toi.
///
/// Traduit la « Lever allow/suppress matrix » de
/// la note de conception du classifieur (voir `BottleneckClassifier`) pour les leviers que ce produit
/// possède réellement. Pure et totale : (état mesuré × identifiant de levier) → suppression ou non.
///
/// ══ ce qu'une suppression est, et ce qu'elle n'est pas ══
///
/// Elle supprime la promesse de gain, jamais le constat. XMP désactivé reste XMP désactivé sur une
/// machine GPU-bound : le réglage est toujours mauvais, il faut toujours le dire, et le registre
/// le compte toujours. Ce qui devient malhonnête, c'est de le vendre comme des images en plus alors
/// que la mesure dit que les images ne viendront pas de là.
///
/// D'où la règle qui gouverne tout ce fichier : **la mesure ne touche pas aux pénalités du registre**, qui décrit la
/// configuration. Une suppression change ce qu'on raconte, jamais ce qu'on compte. Faire l'inverse ferait bouger le
/// registre d'une machine selon qu'un jeu tournait ou non au moment du scan.
/// </summary>
public static class BottleneckLeverPolicy
{
    /// <summary>
    /// Ce levier peut-il encore être vendu comme un gain d'images, vu ce qui a été mesuré ?
    /// Rend <c>null</c> quand rien ne s'y oppose ; sinon le motif dans la langue du docteur, montrable tel quel.
    /// </summary>
    /// <param name="leverId">L'identifiant du levier (<c>xmp</c>, <c>x3d-ccd</c>, …).</param>
    /// <param name="verdict">
    /// Le verdict mesuré. <c>null</c> = aucune mesure : on ne supprime alors rien. Ne pas savoir
    /// n'est pas une raison de taire un réglage : c'est une raison de ne pas promettre.
    /// </param>
    public static string? SuppressionReason(string leverId, BottleneckVerdict? verdict)
    {
        if (verdict is not { } v) return null;

        // Un verdict provisoire (moins de 3 runs) ne supprime rien. La spec réserve les
        // affirmations fortes à trois runs répétables, et supprimer un levier est une affirmation
        // forte : « ça ne t'apportera rien » se trompe aussi lourdement que « ça va te gagner
        // 10 % ». Sur une seule capture, on affiche le verdict et on se tait sur les leviers.
        if (v.Provisional) return null;

        // Le motif mesuré, dans la langue du docteur, suivi de ce qu'il retire.
        var motif = MotifDuGoulot.Dire(v);
        return v.State switch
        {
            // GPU_BOUND : « Suppress: CPU power-plan claims, X3D routing unless separate
            // evidence, RAM FPS claims ». Noter ce qui n'est pas là : `rebar` et `gpu-driver`
            // restent recommandés, ce sont des leviers côté GPU.
            BottleneckState.GpuBound when IsCpuSide(leverId) || IsRamSide(leverId) =>
                Docteur.T("Promesse.GpuSature", motif),

            // CPU_BOUND (les deux intensités) : « Suppress: GPU OC/power/undervolt as FPS gain ».
            // La RAM n'est pas supprimée ici : sur une machine limitée par le CPU, le profil
            // mémoire est justement un des rares leviers qui pèse.
            BottleneckState.CpuBoundStrong or BottleneckState.CpuBoundOrAppBound when IsGpuSide(leverId) =>
                Docteur.T("Promesse.ProcesseurLimite", motif),

            // RAM_COMMIT_PRESSURE : « Suppress: FPS gain claims ». Tous les leviers, sans
            // exception : sur une machine à la limite de validation, le sujet n'est pas les images.
            BottleneckState.RamCommitPressure =>
                Docteur.T("Promesse.Memoire", motif),

            // CAPPED_OR_VSYNC_LIMITED : « Suppress: performance A/B claims from avg FPS ». La
            // moyenne ne peut pas bouger, donc aucun gain ne peut être prouvé, pour aucun levier.
            BottleneckState.CappedOrVsyncLimited =>
                Docteur.T("Promesse.Plafond", motif),

            // THERMAL_OR_POWER_THROTTLE : « Suppress: more power limit/OC until stable thermal
            // headroom proven ». Le pilote reste recommandé (un bug de ventilation se corrige
            // par une mise à jour).
            BottleneckState.ThermalOrPowerThrottle when leverId == LeverIds.ReBar =>
                Docteur.T("Promesse.Chaleur", motif),

            // FRAME_PACING : « Suppress: OC as first answer ». VRR et le cap sont au contraire
            // la première réponse, et le pilote aussi (bug de cadence connu).
            BottleneckState.FramePacing when IsRamSide(leverId) || IsCpuSide(leverId) =>
                Docteur.T("Promesse.Cadence", motif),

            // UNKNOWN_PARTIAL : « Recommend only config-safe items », et surtout « Suppress: any
            // measured-gain claim ». On ne supprime aucun levier (ils restent des corrections de
            // configuration valables) : c'est la promesse chiffrée qui tombe, et elle tombe déjà
            // par BottleneckVerdict.AllowsMeasuredGainClaim.
            _ => null,
        };
    }

    /// <summary>Les identifiants de levier, en un seul endroit : une faute de frappe ici
    /// supprimerait silencieusement... rien du tout, ce qui est le pire des échecs.</summary>
    public static class LeverIds
    {
        public const string Xmp = "xmp";
        public const string GpuDriver = "gpu-driver";
        public const string X3dCcd = "x3d-ccd";
        public const string ReBar = "rebar";
        public const string Vrr = "vrr";
        public const string Hags = "hags";
    }

    // Le levier agit-il côté CPU / ordonnancement ? (Pas de `power-plan` : le parking est identique
    // entre les plans.)
    private static bool IsCpuSide(string leverId) => leverId is LeverIds.X3dCcd;

    // Côté mémoire ?
    private static bool IsRamSide(string leverId) => leverId is LeverIds.Xmp;

    // Côté GPU ? `vrr` et `hags` en sont volontairement exclus : ce sont des leviers de latence et
    // de cadence, que la spec recommande sur une machine CPU-bound au lieu de les supprimer.
    private static bool IsGpuSide(string leverId) =>
        leverId is LeverIds.ReBar or LeverIds.GpuDriver;
}
