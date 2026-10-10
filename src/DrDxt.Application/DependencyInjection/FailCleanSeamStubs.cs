using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.DependencyInjection;

// -----------------------------------------------------------------------
// M2 H2 — fail-clean stub implementations of the SDK seams.
//
// The real P/Invoke implementations (NVAPI / ADLX / registry / power / restore
// point / display) live in Detection / Infrastructure and arrive incrementally.
// Until then the DI container needs SOMETHING to inject so the application graph
// resolves — and the golden rule (master §1.2, §9.2) is fail-clean, never
// fail-open: every seam here reports "unavailable" so a tweak skips cleanly
// (TWEAK.SDK_UNAVAILABLE) and nothing privileged ever happens by accident.
//
// In particular StubElevatedClient REFUSES every op (it performs no privileged
// write at all), and StubRestorePointService reports restore DISABLED so the
// engine BLOCKS before applying anything. This is the safe default: a half-wired
// build can read and plan, but cannot apply.
//
// These are registered with TryAdd* so the App composition root can replace any
// of them with the real implementation without touching this file.
// -----------------------------------------------------------------------

/// <summary>Fail-clean IElevatedClient: refuses every op (no privileged write).
/// The real signed-IPC client is wired in the App host.</summary>
internal sealed class StubElevatedClient : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
        Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed,
            "Elevated helper is not wired in this build (fail-clean stub)."));
}

/// <summary>Fail-clean restore-point seam: reports restore DISABLED so the safety
/// manager / engine BLOCK before any apply (never proceed without a real point).</summary>
internal sealed class StubRestorePointService : IRestorePointService
{
    public bool IsEnabled() => false;
    public bool TryEnable() => false;
    public DateTime? DernierPointDepuis(DateTime depuisUtc) => null;
}

/// <summary>Fail-clean consent prompt: declines (no UI in a headless graph). The
/// App host replaces this with a real dialog-backed prompt.</summary>
internal sealed class StubConsentPrompt : IConsentPrompt
{
    public bool AskEnableRestore() => false;
}

/// <summary>Fail-clean NVAPI latency seam: not available.</summary>
internal sealed class StubNvApi : INvApi
{
    public bool IsAvailable => false;
    public Result<uint> GetLowLatencyMode() => Unavailable<uint>();
    public Result<uint> GetMaxPreRenderedFrames() => Unavailable<uint>();
    public Result<uint> GetVsyncMode() => Unavailable<uint>();
    public Result<uint> GetFrameRateLimit() => Unavailable<uint>();
    public Result<DrsOriginState> GetFrameRateLimitOrigin() => Unavailable<DrsOriginState>();
    public Result<uint> GetPreferredPstate() => Unavailable<uint>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI not available (stub).");
}

/// <summary>Fail-clean ADLX latency seam: not available.</summary>
internal sealed class StubAdlx : IAdlx
{
    public bool IsAvailable => false;
    public Result<int> GetAntiLagState() =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available (stub).");
}

/// <summary>Fail-clean display seam: cannot determine the game display.</summary>
internal sealed class StubDisplayInfo : IDisplayInfo
{
    public Result<DisplayInfo> GetPrimaryGameDisplay() =>
        Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "Display info not available (stub).");

    public Result<IReadOnlyList<DisplayInfo>> GetAllDisplays() =>
        Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable,
            "Display info not available (stub).");
}

/// <summary>Fail-clean registry-read seam: read error (never "absent", which would
/// be a real captured value).</summary>
internal sealed class StubRegistryReader : IRegistryReader
{
    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) =>
        Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");

    public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");
}

/// <summary>Fail-clean NVAPI GPU-identity seam: no NVIDIA GPU / not available.</summary>
internal sealed class StubNvApiInfo : INvApiInfo
{
    public bool IsAvailable => false;
    public Result<NvGpuIdentity> GetGpuIdentity(int gpuIndex) =>
        Result<NvGpuIdentity>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI GPU info not available (stub).");
}

/// <summary>Fail-clean NVAPI GPU-tuning seam: not available.</summary>
internal sealed class StubNvApiGpuTuning : INvApiGpuTuning
{
    public bool IsAvailable => false;
    public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetMemoryClockOffsetMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetPowerLimitPct(int gpuIndex) => Unavailable<int>();
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Unavailable<FanCurve>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI GPU tuning not available (stub).");
}

/// <summary>Fail-clean ADLX GPU-tuning seam: not available.</summary>
internal sealed class StubAdlxGpuTuning : IAdlxGpuTuning
{
    public bool IsAvailable => false;
    public Result<int> ResolveTunableGpuIndex() => Unavailable<int>();
    public Result<int> GetUndervoltValue(int gpuIndex) => Unavailable<int>();
    public Result<int> GetGfxMaxFreqMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetPowerLimitPct(int gpuIndex) => Unavailable<int>();
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Unavailable<FanCurve>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX GPU tuning not available (stub).");
}

/// <summary>Fail-clean GPU load probe: the real D3D11 stress load isn't wired in this
/// build, so it reports CouldNotRun → GpuStabilityTester returns Inconclusive → an
/// undervolt can never be kept (it reverts itself). Replaced by the real D3D11 probe.</summary>
internal sealed class StubGpuLoadProbe : IGpuLoadProbe
{
    public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct) =>
        Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));
}

/// <summary>Fail-clean power-plan read seam: not available.</summary>
internal sealed class StubPowerPlanReader : IPowerPlanReader
{
    public Result<Guid> GetActivePowerPlan() =>
        Result<Guid>.Fail(ErrorCodes.TweakSdkUnavailable, "Power-plan read not available (stub).");
}

/// <summary>Fail-clean CPU power-limit read seam: not available (the App wires the PawnIO reader).</summary>
internal sealed class StubCpuPowerLimitReader : ICpuPowerLimitReader { }

/// <summary>Fail-clean service-state read seam: not available.</summary>
internal sealed class StubServiceStateReader : IServiceStateReader
{
    public Result<ServiceState> GetServiceState(string serviceName) =>
        Result<ServiceState>.Fail(ErrorCodes.TweakSdkUnavailable, "Service-state read not available (stub).");
}

/// <summary>Fail-clean : le mode de l'écran n'est pas lu, et rien n'est posé.</summary>
internal sealed class StubModeEcran : IModeEcran
{
    private const int DispChangeFailed = -1;
    public ModeEcran? Courant(string peripherique) => null;
    public int Tester(ModeEcran mode) => DispChangeFailed;
    public int Ecrire(ModeEcran mode, bool enregistrer) => DispChangeFailed;
}

/// <summary>Fail-closed : sans fenêtre pour demander, le nouveau mode n'est pas gardé.</summary>
internal sealed class StubConfirmationEcran : IConfirmationEcran
{
    public Task<bool> GarderAsync(int hz, TimeSpan delai) => Task.FromResult(false);
}
