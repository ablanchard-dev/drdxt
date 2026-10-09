namespace DrDxt.Bios;

/// <summary>
/// Centralized resx keys for BIOS Doctor findings (FR+en live in the shared
/// Infrastructure resx). No UI text is hard-coded in the advisor — only these
/// keys, mirroring the <c>ErrorCodes</c> discipline.
/// </summary>
internal static class BiosStringKeys
{
    // XMP / EXPO
    public const string XmpOffTitle = "Bios.Xmp.Off.Title";
    public const string XmpOffRationale = "Bios.Xmp.Off.Rationale";
    public const string XmpEnableAction = "Bios.Xmp.Action.Enable";
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
    public const string X3dCppcRouteAction = "Bios.X3dCppc.Action.SetRoute";

    // PCIe link
    public const string PcieNotReadTitle = "Bios.Pcie.NotRead.Title";
    public const string PcieNotReadRationale = "Bios.Pcie.NotRead.Rationale";

    // BIOS / AGESA version
    public const string BiosOldTitle = "Bios.Version.Old.Title";
    public const string BiosOldRationale = "Bios.Version.Old.Rationale";
    public const string BiosUpdateAction = "Bios.Version.Action.Update";

    // Chassis gating
    public const string NotApplicableTitle = "Bios.NotApplicable.Title";
    public const string NotApplicableRationale = "Bios.NotApplicable.Rationale";
    public const string NoneAction = "Bios.Action.None";

    // ── Honest gain estimates: each is a sourced RANGE + caveat (decision §2/§5),
    //    never a bare promised point-% (mirrors M6's GainEvidence discipline). ──
    public const string GainXmpSource = "Bios.Gain.Xmp.Source";
    public const string GainXmpCaveat = "Bios.Gain.Xmp.Caveat";
    public const string GainReBarSource = "Bios.Gain.ReBar.Source";
    public const string GainReBarCaveat = "Bios.Gain.ReBar.Caveat";
    public const string GainX3dSource = "Bios.Gain.X3d.Source";
    public const string GainX3dCaveat = "Bios.Gain.X3d.Caveat";
}
