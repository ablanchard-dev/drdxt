namespace DrDxt.Bios;

/// <summary>
/// Resx keys for the personalized GUIDE content (steps + recovery + labels). FR+EN
/// live in the shared Infrastructure resx; the generator emits only these keys, never
/// hard-coded UI text (mirrors the <c>ErrorCodes</c> / <c>BiosStringKeys</c> discipline).
/// </summary>
public static class BiosGuideStringKeys
{
    // ── Per-setting instruction steps (the exact path/value is carried separately on
    //    GuideStep.ExactValue; the key is the human sentence around it). ──
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

    // ── §16: CSM-off suppressed on Legacy boot — a warning step replaces it. ──
    public const string CsmSuppressedLegacy = "Bios.Guide.Csm.SuppressedLegacy";

    // ── Recovery ladder (top AND bottom, in this exact order — D2). ──
    public const string RecoveryNoBootNormal = "Bios.Guide.Recovery.NoBootNormal";
    public const string RecoveryMemoryTraining = "Bios.Guide.Recovery.MemoryTraining";
    public const string RecoveryClearCmos = "Bios.Guide.Recovery.ClearCmos";
    public const string RecoveryLoadDefaults = "Bios.Guide.Recovery.LoadDefaults";
    public const string RecoveryOneChangeAtATime = "Bios.Guide.Recovery.OneChangeAtATime";

    /// <summary>D4 : le CSM ne fait plus partie du pas a pas (risque de non-demarrage), mais il
    /// reste la cause n°1 d'un ReBAR qui refuse de s'activer. On le nomme sans le faire faire.</summary>
    public const string RecoveryRebarNeedsCsmOff = "Bios.Guide.Recovery.RebarNeedsCsmOff";
}
