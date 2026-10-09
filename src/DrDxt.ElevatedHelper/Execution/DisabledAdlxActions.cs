using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledAdlxActions — a fail-clean ADLX (AMD) seam for the BOOT reconciler, the
// AMD mirror of DisabledNvApiActions.
//
// The boot reconciler (InProcessElevatedClient, running as LocalSystem) must be
// STRUCTURALLY incapable of touching the GPU — on an AMD box just as on an NVIDIA one.
// ONE upstream guard exists (the reconcilable catalog/mapper never emit a GPU op), so this
// seam is the second and LAST line: even if a future change let an ADLX op through, the
// boot service has no real ADLX implementation wired and the op fails clean rather than
// writing a volatile offset at boot. Wiring this also keeps the SYSTEM boot service from
// loading the native ADLX code path at all.
//
// 11/09 — this header used to claim a THIRD guard, "OpAllowlist would gate one". It does
// Not: ValidateAdlx accepts any well-formed ApplyAdlxTuningOp (index >= 0, known Kind) and
// returns Ok. It bounds the SHAPE of an op, never the CONTEXT it runs in — the same
// allowlist serves the user-triggered helper, which must be allowed to write. The refusal
// at boot rests entirely on the wiring below, and is pinned by a test, not by prose.
//
// Rationale: GPU clock/power offsets are VOLATILE (wiped by a reboot) and a marginal one
// could regress boot stability — the reconciler persists only VALIDATED, durable settings
// (registry / power-plan / service), never GPU tuning. SetTuning always returns a clean
// Result.Fail and never throws (the contract for every GPU seam).
// -----------------------------------------------------------------------
public sealed class DisabledAdlxActions : IADLXTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, Message);
}
