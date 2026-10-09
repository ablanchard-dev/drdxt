using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.Detection.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiLatencyReader — the real INvApi read seam over the verified NvApiNative boundary.
//
// Replaces the fail-clean StubNvApi so the latency tweaks (VrrVsyncCap, RenderQueueCap,
// PreferMaxPerf) capture their real original DRS value for a faithful revert. Each DRS getter
// reads the driver setting via NvApiNative.TryGetDrsGlobalSetting — a single NON-elevated NVAPI
// DRS session (CreateSession -> LoadSettings -> GetBaseProfile -> GetSetting -> DestroySession),
// live-validated on an RTX 4060. Setting ids are verified verbatim against NvApiDriverSettings.h
// and enforced by OpAllowlist. Low Latency Mode is not a DRS setting — it is the NVTweak
// registry DWORD the Reflex tweak writes, so it is read from there.
//
// FAIL-CLEAN: no NVIDIA GPU / driver -> IsAvailable false and each read Fail(TWEAK.SDK_UNAVAILABLE).
// A setting absent from the profile (NVAPI_SETTING_NOT_FOUND) also Fails — never a fabricated 0.
// -----------------------------------------------------------------------
public sealed class NvApiLatencyReader : INvApi
{
    // DRS setting ids — verified vs NvApiDriverSettings.h (github.com/NVIDIA/nvapi).
    private const uint VsyncModeId = 0x00A879CF;        // VSYNCMODE_ID
    private const uint FrameRateLimitId = 0x10835002;   // FRL_FPS_ID
    private const uint PreRenderLimitId = 0x007BA09E;   // PRERENDERLIMIT_ID
    private const uint PreferredPstateId = 0x1057EB71;  // PREFERRED_PSTATE_ID
    private const uint VrrModeId = 0x1194F158;          // VRR_MODE_ID (« Configurer G-SYNC »)

    // Low Latency Mode lives in the NVTweak registry, not DRS (mirrors NvidiaUltraLowLatencyTweak).
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string LowLatencyName = "LowLatencyMode";

    private readonly INvApiNative _native;
    private readonly IRegistryReader _registry;

    public NvApiLatencyReader(INvApiNative native, IRegistryReader registry)
        => (_native, _registry) = (native, registry);

    public bool IsAvailable => _native.IsAvailable;

    public Result<uint> GetVsyncMode() => ReadDrs(VsyncModeId);
    public Result<uint> GetFrameRateLimit() => ReadDrs(FrameRateLimitId);
    public Result<uint> GetMaxPreRenderedFrames() => ReadDrs(PreRenderLimitId);
    public Result<uint> GetPreferredPstate() => ReadDrs(PreferredPstateId);
    public Result<uint> GetGsyncMode() => ReadDrs(VrrModeId);

    public Result<uint> GetLowLatencyMode()
    {
        var r = _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName);
        if (!r.IsOk)
            return Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "Low Latency Mode not readable.");
        return Result<uint>.Ok(r.Value ?? 0); // value absent = driver default (Off = 0).
    }

    /// <summary>The frame cap AND where the value comes from. The plain <see
    /// cref="GetFrameRateLimit"/> answers "what is it"; this answers "who put it there", which
    /// is the question that separates a value NVIDIA ships from one someone chose.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetFrameRateLimitWithOrigin()
    {
        if (!_native.TryReadDrsGlobalSetting(FrameRateLimitId, out var r))
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "NVIDIA driver setting not readable (NVAPI unavailable or setting at default).");

        return Result<(uint, DrsOriginState)>.Ok((r.Value,
            DrsSettingOrigin.Describe(r.SettingLocation, r.IsCurrentPredefined, r.IsPredefinedValid)));
    }

    /// <summary>Seam-facing origin read: the same DRS read, keeping only the provenance.</summary>
    public Result<DrsOriginState> GetFrameRateLimitOrigin()
    {
        var r = GetFrameRateLimitWithOrigin();
        return r.IsOk
            ? Result<DrsOriginState>.Ok(r.Value.Origin)
            : Result<DrsOriginState>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
    }

    /// <summary>The frame cap the driver applies to ONE game, with its provenance.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetFrameRateLimitForApplication(string applicationPath)
        => GetDrsForApplication(FrameRateLimitId, applicationPath);

    /// <summary>Any DRS setting as the driver applies it to ONE game, with its provenance.
    /// Fails when the executable has no profile at all (the driver does not fall back to the base
    /// profile there). When the profile exists but does not own the setting, the driver returns
    /// the INHERITED value with settingLocation = GLOBAL_PROFILE (measured on cod.exe, 10/09/2026):
    /// the origin says so, and callers must read it with the value.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
    {
        // A bare file name makes the driver answer EXECUTABLE_PATH_IS_AMBIGUOUS; refuse it here
        // so the failure names the real cause instead of surfacing as a driver error.
        if (string.IsNullOrWhiteSpace(applicationPath) || !Path.IsPathFullyQualified(applicationPath))
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "A fully qualified executable path is required to find its driver profile.");

        var lecture = _native.LireDrsAuJeu(applicationPath, settingId, out var r);
        if (lecture == INvApiNative.LectureAuJeu.PasDeProfil)
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile,
                "This executable has no driver profile of its own: the driver applies the global one.");
        if (lecture != INvApiNative.LectureAuJeu.Lu)
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "The game's driver profile could not be read.");

        return Result<(uint, DrsOriginState)>.Ok((r.Value,
            DrsSettingOrigin.Describe(r.SettingLocation, r.IsCurrentPredefined, r.IsPredefinedValid)));
    }

    private Result<uint> ReadDrs(uint settingId) =>
        _native.TryGetDrsGlobalSetting(settingId, out var value)
            ? Result<uint>.Ok(value)
            : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable,
                "NVIDIA driver setting not readable (NVAPI unavailable or setting at default).");
}
