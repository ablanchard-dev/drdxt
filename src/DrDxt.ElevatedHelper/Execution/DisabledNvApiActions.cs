using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledNvApiActions — a fail-clean NVAPI seam for the BOOT reconciler.
//
// The boot reconciler (InProcessElevatedClient, running as LocalSystem) must be
// STRUCTURALLY incapable of touching the GPU. ONE upstream guard exists (the reconcilable
// catalog/mapper never emit a GPU op), so this seam is the second and LAST line: even if a
// future change let a GPU op through, the boot service has no real NVAPI implementation
// wired and the op fails clean rather than writing a volatile offset at boot. Wiring this
// also keeps the SYSTEM boot service from loading the native NVAPI code path at all.
//
// 11/09 — this header used to claim a THIRD guard, "OpAllowlist would gate one". It does
// Not. Validate() receives only the OP: ValidateNvApiPowerLimit accepts any 0..100 percent
// on a non-negative index, and the same allowlist serves the user-triggered helper, which
// must be allowed to write. It bounds the SHAPE of an op, never the CONTEXT it runs in.
// The refusal at boot therefore rests entirely on the wiring below — which is why it is now
// pinned by a test (HostCompositionTests, the GPU twin of the firmware one), not by prose.
//
// Rationale: GPU clock/power offsets are VOLATILE (wiped by a reboot) and a marginal
// one could regress boot stability — the reconciler persists only VALIDATED, durable
// settings (registry / power-plan / service), never GPU tuning. Every method returns a
// clean Result.Fail and never throws (the contract for both NVAPI seams).
// -----------------------------------------------------------------------
public sealed class DisabledNvApiActions : INvApiDrsActions, INvApiTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetGlobalSetting(uint settingId, uint value) => Fail();

    // L2 : le reconciliateur de boot n'ecrit pas plus par jeu que globalement. Meme refus, meme
    // motif -- un chemin qui refuse pour une raison differente serait un chemin de plus a auditer.
    public Result SetSettingForApplication(string applicationPath, uint settingId, uint value) => Fail();

    // F1 lot 3 : ni ecriture ni suppression par jeu au boot — meme refus, meme motif.
    public Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue) => Fail();

    public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => FailInt();

    public Result<int> GetPowerLimitPercent(int gpuIndex) => FailInt();

    public Result SetCoreClockOffset(int gpuIndex, int offsetMHz) => Fail();

    public Result SetPowerLimitPercent(int gpuIndex, float pct) => Fail();

    private static Result Fail() => Result.Fail(ErrorCodes.HelperOpFailed, Message);

    private static Result<int> FailInt() => Result<int>.Fail(ErrorCodes.HelperOpFailed, Message);
}
