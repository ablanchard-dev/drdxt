using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// BIOS Doctor foundation: a pure read+diff advisor. It consumes the
/// <see cref="HardwareInventory"/> + <see cref="SettingsState"/> already produced
/// by M1 (it never re-detects them, never re-runs XMP inference, never writes the
/// BIOS) and emits only the firmware <see cref="BiosFinding"/>s that concern this
/// machine.
///
/// Honesty (decision §9 / §22.3, plan §0.7):
///   • a setting we can read and is mis-set → an actionable <see cref="BiosMatrixClass.Guide"/>
///     Finding (V1 = guide, never write);
///   • a setting we cannot read → an honest <see cref="BiosMatrixClass.Read"/> Finding flagged
///     <see cref="BiosFinding.StateNotReadable"/> — never a fabricated state;
///   • an already-good machine → no actionable Finding (a valid, assumed result).
///
/// Safety (plan B2): CSM-off is never advised on a Legacy boot (no-boot risk).
/// Gating (§16): laptop/handheld BIOS is locked → no actionable Findings.
/// </summary>
public sealed class BiosAdvisor : IBiosAdvisor
{
    // BIOS older than this is informationally flagged "consider updating". We have
    // NO motherboard knowledge base in the foundation (that is a later increment),
    // so this is a soft, honest hint — never a hard AGESA/version claim.
    private static readonly TimeSpan OldBiosThreshold = TimeSpan.FromDays(365 * 2);

    public Result<IReadOnlyList<BiosFinding>> Advise(HardwareInventory hw, SettingsState state)
    {
        // ── Defensive boundary: reject internally incoherent input, fail clean. ──
        if (hw.Memory is null || hw.Memory.Count == 0)
            return Result<IReadOnlyList<BiosFinding>>.Fail(
                ErrorCodes.BiosInputInvalid, "Empty memory inventory; cannot diff XMP/EXPO.");
        if (hw.Gpus is null || hw.Gpus.Count == 0)
            return Result<IReadOnlyList<BiosFinding>>.Fail(
                ErrorCodes.BiosInputInvalid, "No GPU in inventory; cannot diff ReBAR/PCIe.");

        var findings = new List<BiosFinding>();

        // ── §16 chassis gating: laptop/handheld BIOS is typically locked. ──
        // Surface ONE honest informational Finding rather than impossible steps.
        if (hw.Chassis is ChassisType.Laptop or ChassisType.Handheld)
        {
            findings.Add(new BiosFinding(
                Kind: FirmwareSettingKind.BiosVersion,
                Severity: Severity.Info,
                TitleKey: BiosStringKeys.NotApplicableTitle,
                RationaleKey: BiosStringKeys.NotApplicableRationale,
                MatrixClass: BiosMatrixClass.Read,
                CurrentState: TriState.Unknown,
                RecommendedAction: BiosRecommendedAction.None,
                RecommendedActionKey: BiosStringKeys.NoneAction,
                Gain: null,
                StateNotReadable: false));
            return Result<IReadOnlyList<BiosFinding>>.Ok(findings);
        }

        AddXmpExpo(findings, state);
        AddReBarChain(findings, hw, state);
        AddX3dCppc(findings, hw);
        AddPcieGen(findings);
        AddBiosVersion(findings, hw);

        return Result<IReadOnlyList<BiosFinding>>.Ok(findings);
    }

    // ── XMP / EXPO (consumes M1's inferred state; we never re-infer here). ──
    private static void AddXmpExpo(List<BiosFinding> findings, SettingsState state)
    {
        switch (state.XmpExpo)
        {
            case TriState.Off:
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.XmpExpo, Severity.Major,
                    BiosStringKeys.XmpOffTitle, BiosStringKeys.XmpOffRationale,
                    BiosMatrixClass.Guide, TriState.Off,
                    BiosRecommendedAction.Enable, BiosStringKeys.XmpEnableAction,
                    // 23/09/2026 : plus de fourchette. Mesures publiques : de quelques % (jeu limite
                    // par la carte graphique) a ~42 % (jeu tres sensible a la memoire), ~1 % sur un
                    // X3D (TechSpot, XDA, AMD). Aucune plage unique ne decrit une machine : le
                    // chiffre de la personne vient de l'A/B du produit. La mise en garde le dit.
                    Gain: new BiosGainEstimate(
                        MinPct: null, MaxPct: null, Conditional: false,
                        SourceKey: BiosStringKeys.GainXmpSource, CaveatKey: BiosStringKeys.GainXmpCaveat),
                    StateNotReadable: false));
                break;

            case TriState.Unknown:
                // Honest "not read": we report the unknown, we never assert OFF.
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.XmpExpo, Severity.Info,
                    BiosStringKeys.XmpUnknownTitle, BiosStringKeys.XmpUnknownRationale,
                    BiosMatrixClass.Read, TriState.Unknown,
                    BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                    Gain: null, StateNotReadable: true));
                break;

            // On → nothing (no fabricated need).
        }
    }

    // ── ReBAR + ordered prerequisites (Above-4G → CSM → Re-Size BAR). ──
    private static void AddReBarChain(List<BiosFinding> findings, HardwareInventory hw, SettingsState state)
    {
        // Prefer the per-GPU ReBAR read (the primary GPU); fall back to the
        // settings-level read. Both come straight from M1 — no re-detection.
        var primary = hw.Gpus[0];
        var rebar = primary.ReBar != TriState.Unknown ? primary.ReBar : state.ReBar;
        var isNvidia = primary.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                       || primary.Vendor.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

        switch (rebar)
        {
            case TriState.Off:
                // Ordered prerequisites first so the chain reads top-to-bottom.
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.Above4G, Severity.Minor,
                    BiosStringKeys.Above4GTitle, BiosStringKeys.Above4GRationale,
                    BiosMatrixClass.Guide, TriState.Unknown,
                    BiosRecommendedAction.Enable, BiosStringKeys.Above4GEnableAction,
                    Gain: null, StateNotReadable: false));

                // §16/B2: CSM-off is only safe to advise when booting UEFI.
                // Legacy/MBR → suppress the step (no-boot risk), keep the rest.
                if (hw.Bios.IsUefi)
                    findings.Add(new BiosFinding(
                        FirmwareSettingKind.Csm, Severity.Minor,
                        BiosStringKeys.CsmTitle, BiosStringKeys.CsmRationale,
                        BiosMatrixClass.Guide, TriState.Unknown,
                        BiosRecommendedAction.Disable, BiosStringKeys.CsmDisableAction,
                        Gain: null, StateNotReadable: false));

                findings.Add(new BiosFinding(
                    FirmwareSettingKind.ReBar, Severity.Minor,
                    BiosStringKeys.ReBarOffTitle, BiosStringKeys.ReBarOffRationale,
                    BiosMatrixClass.Guide, TriState.Off,
                    BiosRecommendedAction.Enable, BiosStringKeys.ReBarEnableAction,
                    // decision §2: ReBAR is game-dependent and can REGRESS some titles —
                    // no honest global %. Conditional, with a "verify per game" caveat.
                    Gain: new BiosGainEstimate(
                        MinPct: null, MaxPct: null, Conditional: true,
                        SourceKey: BiosStringKeys.GainReBarSource, CaveatKey: BiosStringKeys.GainReBarCaveat),
                    StateNotReadable: false));
                break;

            case TriState.Unknown:
                // §16: recommend-with-caveat, honest unknown, never Major, never force,
                // and never drag in the CSM/Above-4G write steps.
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.ReBar, Severity.Info,
                    BiosStringKeys.ReBarUnknownTitle, BiosStringKeys.ReBarUnknownRationale,
                    BiosMatrixClass.Read, TriState.Unknown,
                    BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                    Gain: null, StateNotReadable: true));
                break;

            // On → nothing.
        }

        _ = isNvidia; // vendor distinction already captured by per-GPU read vs Unknown.
    }

    // ── X3D / CPPC routing — only matters on AMD multi-CCD X3D. ──
    // decision §2: the PRIMARY routing fix is Windows-side (AMD 3D V-Cache service /
    // Game Bar), and BIOS CPPC only matters "if mis-set". We cannot read the routing
    // state from M1, so we do not blare a Major BIOS write step — we surface a Verify
    // finding that LEADS with the Windows-side check (the rationale resx) and treats the
    // BIOS CPPC path as a secondary "only if mis-set" step.
    private static void AddX3dCppc(List<BiosFinding> findings, HardwareInventory hw)
    {
        if (hw.Cpu.IsX3D && hw.Cpu.CcdCount > 1)
            findings.Add(new BiosFinding(
                FirmwareSettingKind.X3dCppc, Severity.Minor,
                BiosStringKeys.X3dCppcTitle, BiosStringKeys.X3dCppcRationale,
                BiosMatrixClass.Read, TriState.Unknown,
                BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                // Gain seulement si le routage des CCD est mal regle ; le plus souvent deja correct.
                // 23/09/2026 : plus de fourchette « 0–10 % », tiree de nos notes et non d'une mesure
                // publique. Conditionnel, et c'est la mise en garde qui porte la nuance.
                Gain: new BiosGainEstimate(
                    MinPct: null, MaxPct: null, Conditional: true,
                    SourceKey: BiosStringKeys.GainX3dSource, CaveatKey: BiosStringKeys.GainX3dCaveat),
                StateNotReadable: true));
    }

    // ── PCIe link — not in M1 inventory → honest "not read", never fabricated. ──
    private static void AddPcieGen(List<BiosFinding> findings)
    {
        findings.Add(new BiosFinding(
            FirmwareSettingKind.PcieGen, Severity.Info,
            BiosStringKeys.PcieNotReadTitle, BiosStringKeys.PcieNotReadRationale,
            BiosMatrixClass.Read, TriState.Unknown,
            BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
            Gain: null, StateNotReadable: true));
    }

    // ── BIOS / AGESA version — soft, honest hint only (no KB in the foundation). ──
    private static void AddBiosVersion(List<BiosFinding> findings, HardwareInventory hw)
    {
        // No release date → we cannot judge age honestly → no finding (never fabricate).
        if (hw.Bios.ReleaseDate is not { } released) return;

        var age = DateTimeOffset.UtcNow - released;
        if (age <= OldBiosThreshold) return;

        findings.Add(new BiosFinding(
            FirmwareSettingKind.BiosVersion, Severity.Info,
            BiosStringKeys.BiosOldTitle, BiosStringKeys.BiosOldRationale,
            BiosMatrixClass.Read, TriState.Unknown,
            BiosRecommendedAction.Update, BiosStringKeys.BiosUpdateAction,
            Gain: null, StateNotReadable: false));
    }
}
