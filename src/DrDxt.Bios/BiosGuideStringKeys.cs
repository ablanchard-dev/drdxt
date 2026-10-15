namespace DrDxt.Bios;

/// <summary>
/// Clés resx du contenu de guide personnalisé (étapes + récupération + libellés). FR et EN vivent
/// dans la resx partagée d'Infrastructure ; le générateur n'émet que ces clés, jamais de texte
/// d'interface en dur (même discipline que <c>ErrorCodes</c> / <c>BiosStringKeys</c>).
/// </summary>
public static class BiosGuideStringKeys
{
    // ── Étapes d'instruction par réglage (le chemin / la valeur exacts sont portés à part par
    //    GuideStep.ExactValue ; la clé est la phrase humaine autour). ──
    public const string StepXmp = "Bios.Guide.Step.Xmp";
    public const string StepAbove4G = "Bios.Guide.Step.Above4G";
    public const string StepCsm = "Bios.Guide.Step.Csm";
    public const string StepRebar = "Bios.Guide.Step.ReBar";
    public const string StepX3dCppc = "Bios.Guide.Step.X3dCppc";
    public const string StepPcie = "Bios.Guide.Step.Pcie";
    public const string StepBiosVersion = "Bios.Guide.Step.BiosVersion";
    public const string StepGeneric = "Bios.Guide.Step.Generic";

    // ── Confidence labels (header). ──
    public const string Verified = "Bios.Guide.Confidence.Verified";
    public const string UnverifiedFallback = "Bios.Guide.Confidence.Unverified";

    // ── Désactivation du CSM retirée sur démarrage Legacy : une étape d'avertissement la remplace. ──
    public const string CsmSuppressedLegacy = "Bios.Guide.Csm.SuppressedLegacy";

    // ── Échelle de récupération (en haut et en bas, dans cet ordre exact). ──
    public const string RecoveryNoBootNormal = "Bios.Guide.Recovery.NoBootNormal";
    public const string RecoveryMemoryTraining = "Bios.Guide.Recovery.MemoryTraining";
    public const string RecoveryClearCmos = "Bios.Guide.Recovery.ClearCmos";
    public const string RecoveryLoadDefaults = "Bios.Guide.Recovery.LoadDefaults";
    public const string RecoveryOneChangeAtATime = "Bios.Guide.Recovery.OneChangeAtATime";

    /// <summary>Le CSM ne fait pas partie du pas a pas (risque de non-demarrage), mais il
    /// reste la cause n°1 d'un ReBAR qui refuse de s'activer. On le nomme sans le faire faire.</summary>
    public const string RecoveryRebarNeedsCsmOff = "Bios.Guide.Recovery.RebarNeedsCsmOff";
}
