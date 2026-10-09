using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Injectable hardware seams for the M2 Phase E GPU-tuning tweaks (E2 undervolt,
// E3 power-limit, E4 fan-curve).
//
// Like the latency seams, these are read + CAPABILITY seams only — they never
// perform the privileged write. The change is always applied through
// IElevatedClient with a typed, allowlisted PrivilegedOp (Phase A). The seams let
// each tweak capture the real original value before applying (for a faithful
// revert) and decide gating.
//
// Every method returns a Result so an unavailable SDK / GPU fails CLEAN
// (TWEAK.SDK_UNAVAILABLE) — it must never throw. Implementations live in
// Detection / Infrastructure (P/Invoke); unit tests use NSubstitute fakes.
//
// SDK split is deliberate, mirroring Option A (master §11): NVIDIA = clock OFFSET
// + power limit (no V/F curve); AMD = ADLX tuning. The seams are separate from
// the latency seams (INvApi / IAdlx) so GPU tuning concerns don't leak there.
// -----------------------------------------------------------------------

/// <summary>Read seam over the NVIDIA driver (NVAPI) for GPU tuning. Apply is done
/// via IElevatedClient typed ops; this only reads the current values.</summary>
public interface INvApiGpuTuning
{
    /// <summary>True when an NVIDIA driver / NVAPI is present and usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the current applied core-clock offset (MHz) for a GPU.
    /// Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetCoreClockOffsetMhz(int gpuIndex);

    /// <summary>Reads the current applied memory-clock offset (MHz) for a GPU.
    /// Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetMemoryClockOffsetMhz(int gpuIndex);

    /// <summary>Reads the current power-limit (% of TDP) for a GPU.
    /// Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetPowerLimitPct(int gpuIndex);

    /// <summary>Reads the current fan curve for a GPU. Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Gpu.FanCurve> GetFanCurve(int gpuIndex);
}

/// <summary>NVIDIA GPU architecture / generation, coarse enough to GATE voltage
/// writes. NVIDIA removed direct V/F-curve voltage writes on Blackwell (RTX 50) —
/// so a later gate disables true voltage-undervolt there (recipe "Blackwell /
/// RTX 50 lockout"). Ada (RTX 40) and older still honour the legacy writes.</summary>
public enum GpuArchitecture
{
    /// <summary>Could not be determined (no NVIDIA, read failed). Treated
    /// conservatively by gates (no assumption that voltage writes are allowed).</summary>
    Unknown = 0,
    /// <summary>Ada Lovelace (RTX 40) or older — legacy voltage writes honoured.</summary>
    AdaOrOlder = 1,
    /// <summary>Blackwell (RTX 50) — direct voltage-undervolt is LOCKED OUT.</summary>
    Blackwell = 2,

    // ── AMD, ajoutees le 02/09/2026 pour la decision D5 (fail-closed materiel) ──────────────
    // Le classifieur ne parlait que de NVIDIA, et `SupportsVoltageUndervolt` rendait donc
    // `true` pour tout GPU non-NVIDIA. RDNA3 est la seule architecture AMD qui garde le
    // voltage manuel, et elle n'a jamais ete testee en ecriture : elle passait quand meme.

    /// <summary>RDNA 3 (RX 7000). La seule architecture AMD qui garde le voltage manuel, et
    /// elle n'a jamais ete validee en ecriture sur une vraie carte. D5 : voltage desactive
    /// jusqu'a un test reel.</summary>
    Rdna3 = 3,

    /// <summary>RDNA 4 (RX 9000). Validee EN VIE sur une RX 9070 XT : ecriture, revert,
    /// empreinte propre.</summary>
    Rdna4 = 4,

    /// <summary>AMD-TOUT (09/10/2026) : RDNA 2 (RX 6000 : 6400 à 6950 XT). Elles passaient « architecture inconnue » alors
    /// qu'elles sont nombreuses chez les joueurs. Reconnues, lues par ADLX ; jamais validées en écriture (voltage fermé).</summary>
    Rdna2 = 5,
}

/// <summary>One read of a physical NVIDIA GPU's identity for gating: its marketing
/// name and the coarse architecture. Read-only, no privilege needed.</summary>
public sealed record NvGpuIdentity(string Name, GpuArchitecture Architecture);

/// <summary>Read seam over the NVIDIA driver (NVAPI) for GPU IDENTITY — name +
/// architecture/generation — used to GATE voltage-undervolt off on Blackwell.
/// Read-only and unelevated-safe; on an AMD / no-NVIDIA box it reports unavailable
/// and fails clean (never throws).</summary>
public interface INvApiInfo
{
    /// <summary>True when an NVIDIA driver / NVAPI is present and usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the identity (name + architecture) of the GPU at the given
    /// index. Unavailable / no NVIDIA → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<NvGpuIdentity> GetGpuIdentity(int gpuIndex);
}

/// <summary>Shared bound for the tunable-GPU probe loop used by
/// <see cref="IAdlxGpuTuning.ResolveTunableGpuIndex"/>. On an iGPU + dGPU box (e.g. a
/// Ryzen X3D APU at index 0 + a discrete Radeon at index 1) ADLX index 0 is often the
/// NON-tunable iGPU, so the read seam probes a small range of indices and selects the
/// First whose GFX tuning reads — the discrete AMD card. This MIRRORS the AMD test
/// harness picker (tools/amd-harness/DrDxt.AmdHarness/Program.cs) — keep the bound in
/// lock-step; do not invent a different one.</summary>
public static class AdlxGpuProbe
{
    /// <summary>Upper bound (exclusive) for the GPU-index probe — mirrors the harness
    /// picker's <c>for (int i = 0; i &lt; 8; i++)</c>.</summary>
    public const int MaxProbeIndices = 8;
}

/// <summary>Read seam over the AMD ADLX runtime for GPU tuning (undervolt / power
/// limit / fan). Apply is done via IElevatedClient typed ops; this only reads.</summary>
public interface IAdlxGpuTuning
{
    /// <summary>True when the AMD ADLX runtime is present and usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Resolves the index of the first tunable discrete AMD GPU — the one whose
    /// GFX tuning actually reads (<see cref="GetUndervoltValue"/> IsOk) — by probing
    /// indices [0..<see cref="AdlxGpuProbe.MaxProbeIndices"/>). On an iGPU + dGPU box the
    /// iGPU at index 0 is not tunable, so the discrete card (e.g. index 1) is selected.
    /// Mirrors the AMD harness picker. Unavailable / no index reads →
    /// Fail(TWEAK.SDK_UNAVAILABLE) (fail-clean; never throws).</summary>
    Result<int> ResolveTunableGpuIndex();

    /// <summary>Reads the current ADLX undervolt value (the VoltMv the ADLX GFX read
    /// reports). This is used only as an <c>.IsOk</c> readability probe by
    /// <see cref="ResolveTunableGpuIndex"/> (the value is ignored). It must not be used to
    /// build the undervolt revert op — the undervolt apply/revert round-trips an absolute
    /// MaxFreq (see <see cref="GetGfxMaxFreqMhz"/>), never a voltage.
    /// Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetUndervoltValue(int gpuIndex);

    /// <summary>Reads the current absolute GFX maximum clock (MHz) — the value the ADLX
    /// undervolt write actually sets. The undervolt tweak reads this before applying so it
    /// can (a) build the absolute target <c>current + offset</c> it writes and (b) capture
    /// the absolute original as the inverse op, so the revert restores the exact stock
    /// MaxFreq (symmetric with how the power-limit path round-trips an absolute %).
    /// Unavailable / read failure / index &lt; 0 → Fail(TWEAK.SDK_UNAVAILABLE); never throws.</summary>
    Result<int> GetGfxMaxFreqMhz(int gpuIndex);

    /// <summary>Reads the current ADLX power-limit (% of TDP). Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetPowerLimitPct(int gpuIndex);

    /// <summary>Reads the current fan curve for a GPU. Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Gpu.FanCurve> GetFanCurve(int gpuIndex);
}

/// <summary>The outcome of a short GPU stability check.</summary>
public enum StabilityOutcome
{
    /// <summary>The GPU ran the load cleanly — no hang, artifact, or crash.</summary>
    Stable = 0,
    /// <summary>An artifact / hang / driver-reset / crash was observed → the change
    /// must be reverted immediately.</summary>
    Unstable = 1,
    /// <summary>The stability test itself could not run (no GPU load harness) →
    /// treated conservatively (the tweak fails clean, never claims Ok).</summary>
    Inconclusive = 2,
}

/// <summary>Seam over a short GPU stability test (a brief GPU load that watches for
/// hangs / artifacts / driver resets). Used by the undervolt tweak (E2) to verify
/// a freshly-applied offset is stable before returning Ok. Returns a Result so a
/// missing harness fails clean (TWEAK.SDK_UNAVAILABLE) — never throws.</summary>
public interface IStabilityTester
{
    /// <summary>Runs a short GPU load on the given GPU and reports stability.
    /// A crash/artifact must surface as StabilityOutcome.Unstable, not an exception.</summary>
    Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct);
}

/// <summary>The outcome of a brief GPU stress load used to probe undervolt stability.</summary>
public enum GpuLoadResult
{
    /// <summary>The load ran for its full duration and the device stayed healthy.</summary>
    CompletedDeviceHealthy = 0,
    /// <summary>The device was lost / removed / reset (a TDR) during the load → unstable.</summary>
    DeviceLost = 1,
    /// <summary>The load could not run at all (no D3D harness / device creation failed) →
    /// the stability tester treats this conservatively as Inconclusive.</summary>
    CouldNotRun = 2,
}

/// <summary>Seam over a brief, real GPU stress load (a few seconds of GPU work that makes
/// an unstable clock offset surface as a device-lost / TDR). Implemented over D3D11 in
/// Detection and smoke-validated; the orchestration (GpuStabilityTester) is pure and
/// unit-tested with a fake. Returns a Result so a missing harness fails clean; it must
/// Not throw on a device loss — that is GpuLoadResult.DeviceLost.</summary>
public interface IGpuLoadProbe
{
    /// <summary>Runs a brief GPU load on the given GPU for up to <paramref name="duration" />
    /// and reports whether the device survived.</summary>
    Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct);
}
