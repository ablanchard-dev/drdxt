namespace DrDxt.Bios;

/// <summary>
/// Clés resx centralisées des constats du BIOS Doctor (FR et EN vivent dans la resx partagée
/// d'Infrastructure). Aucun texte d'interface en dur dans le conseiller : seulement ces clés, même
/// discipline que <c>ErrorCodes</c>.
/// </summary>
internal static class BiosStringKeys
{
    // XMP / EXPO
    public const string XmpOffTitle = "Bios.Xmp.Off.Title";
    public const string XmpOffRationale = "Bios.Xmp.Off.Rationale";
    public const string XmpEnableAction = "Bios.Xmp.Action.Enable";
    public const string XmpOffUnstableTitle = "Bios.Xmp.Off.Unstable.Title";
    public const string XmpOffUnstableRationale = "Bios.Xmp.Off.Unstable.Rationale";
    public const string XmpUnknownTitle = "Bios.Xmp.Unknown.Title";
    public const string XmpUnknownRationale = "Bios.Xmp.Unknown.Rationale";
    public const string VerifyAction = "Bios.Action.VerifyInBios";

    // ReBAR + prerequisites
    public const string ReBarOffTitle = "Bios.ReBar.Off.Title";
    public const string ReBarOffRationale = "Bios.ReBar.Off.Rationale";
    public const string ReBarEnableAction = "Bios.ReBar.Action.Enable";
    public const string ReBarUnknownTitle = "Bios.ReBar.Unknown.Title";
    public const string ReBarUnknownRationale = "Bios.ReBar.Unknown.Rationale";
    public const string Above4GTitle = "Bios.Above4G.Title";
    public const string Above4GRationale = "Bios.Above4G.Rationale";
    public const string Above4GEnableAction = "Bios.Above4G.Action.Enable";
    public const string CsmTitle = "Bios.Csm.Title";
    public const string CsmRationale = "Bios.Csm.Rationale";
    public const string CsmDisableAction = "Bios.Csm.Action.Disable";

    // X3D / CPPC
    public const string X3dCppcTitle = "Bios.X3dCppc.Title";
    public const string X3dCppcRationale = "Bios.X3dCppc.Rationale";

    // PCIe link
    public const string PcieNotReadTitle = "Bios.Pcie.NotRead.Title";
    public const string PcieNotReadRationale = "Bios.Pcie.NotRead.Rationale";

    // Version BIOS / AGESA
    public const string BiosOldTitle = "Bios.Version.Old.Title";
    public const string BiosOldRationale = "Bios.Version.Old.Rationale";
    public const string BiosUpdateAction = "Bios.Version.Action.Update";

    // Chassis gating
    public const string NotApplicableTitle = "Bios.NotApplicable.Title";
    public const string NotApplicableRationale = "Bios.NotApplicable.Rationale";
    public const string NoneAction = "Bios.Action.None";

    // ── Estimations de gain honnêtes : chacune est une plage sourcée + une réserve, jamais un %
    //    promis isolé (même discipline que le GainEvidence des profils). ──
    public const string GainXmpSource = "Bios.Gain.Xmp.Source";
    public const string GainXmpCaveat = "Bios.Gain.Xmp.Caveat";
    public const string GainReBarSource = "Bios.Gain.ReBar.Source";
    public const string GainReBarCaveat = "Bios.Gain.ReBar.Caveat";
    public const string GainX3dSource = "Bios.Gain.X3d.Source";
    public const string GainX3dCaveat = "Bios.Gain.X3d.Caveat";
}
