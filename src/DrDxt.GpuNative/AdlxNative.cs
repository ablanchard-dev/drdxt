using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// 25/09/2026 : pour tout l'assembly. Le helper eleve charge nvapi64, amdadlx64 et nvml par leur
// nom ; sur une carte de l'autre marque la DLL manque dans System32, et la recherche par defaut
// continuait dans le PATH (dossiers modifiables par l'utilisateur). Les pilotes installent ces
// DLL dans System32 : on ne cherche que la.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxNative — the SINGLE real P/Invoke boundary into amdadlx64.dll.
//
// ARCHITECTURE
// ────────────
// ADLX uses a COM-like C ABI: every object is a struct whose first field is
// a pointer to its vtable (an array of function pointers). We navigate the
// vtable via Marshal.ReadIntPtr — no unsafe blocks needed.
//
// INIT SEQUENCE
// ─────────────
//   ADLXInitialize(1.0.0 version) → IADLXSystem* (never Release this)
//   IADLXSystem::GetGPUs         (slot 1) → IADLXGPUList*
//   IADLXSystem::GetGPUTuningServices (slot 8) → IADLXGPUTuningServices*
//   [per GPU]
//     IsSupportedManualGFXTuning / IsSupportedManualPowerTuning / IsSupportedManualFanTuning
//     GetManualGFXTuning → IADLXManualGraphicsTuning1* → QI → IADLXManualGraphicsTuning2*
//     GetManualPowerTuning → IADLXManualPowerTuning*
//     GetManualFanTuning   → IADLXManualFanTuning*
//
// VTABLE SLOTS — named constants live in AdlxVtableSlots.cs; ADLX-SLOTS.md carries
// the full per-slot citation. All indices verified 2026-06-29 against the canonical
// ADLX SDK headers (https://github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include/*.h):
// ─────────────────────────────────────────────────────────────────────────────────────
//   IADLXSystem (ISystem.h — SPECIAL, no Acquire/Release):
//     0=GetHybridGraphicsType  1=GetGPUs  8=GetGPUTuningServices
//   IADLXGPUList (ISystem.h — with Acquire/Release):
//     0=Acquire  1=Release  3=Size  11=At_GPUList
//   IADLXGPUTuningServices (IGPUTuning.h — with Acquire/Release):
//     0=Acquire  1=Release  8=IsSupportedManualGFXTuning  10=IsSupportedManualFanTuning
//     11=IsSupportedManualPowerTuning  14=GetManualGFXTuning  16=GetManualFanTuning
//     17=GetManualPowerTuning
//   IADLXManualGraphicsTuning1 (IGPUManualGFXTuning.h — states-based, RDNA1/Vega):
//     0=Acquire  1=Release  2=QueryInterface
//     3=GetGPUTuningRanges  4=GetGPUTuningStates  5=GetEmptyGPUTuningStates
//     6=IsValidGPUTuningStates  7=SetGPUTuningStates
//     NB: we never call slots 3-7 — Tuning1 is used only via QueryInterface (slot 2)
//         to obtain Tuning2. Its layout is unrelated to Tuning2's freq/voltage API.
//   IADLXManualGraphicsTuning2 (IGPUManualGFXTuning.h — freq+voltage, RDNA):
//     0=Acquire  1=Release  2=QueryInterface
//     3=GetGPUMinFrequencyRange  4=GetGPUMinFrequency  5=SetGPUMinFrequency
//     6=GetGPUMaxFrequencyRange  7=GetGPUMaxFrequency  8=SetGPUMaxFrequency  (write slot 8)
//     9=GetGPUVoltageRange      10=GetGPUVoltage       11=SetGPUVoltage       (write slot 11)
//   IADLXManualPowerTuning (IGPUManualPowerTuning.h — with Acquire/Release):
//     0=Acquire  1=Release  2=QueryInterface  3=GetPowerLimitRange  4=GetPowerLimit
//     5=SetPowerLimit  (write slot 5)
//   IADLXManualFanTuning (IGPUManualFanTuning.h — with Acquire/Release):
//     0=Acquire  1=Release  3=GetFanTuningRanges  4=GetFanTuningStates
//   IADLXManualFanTuningStateList (IGPUManualFanTuning.h — with Acquire/Release):
//     0=Acquire  1=Release  3=Size  11=At_ManualFanTuningStateList
//   IADLXManualFanTuningState (IGPUManualFanTuning.h — with Acquire/Release):
//     0=Acquire  1=Release  3=GetFanSpeed  5=GetTemperature
//
// Fail-clean contract
// ───────────────────
//   • DllNotFoundException (DLL absent, no AMD driver) → IsAvailable = false.
//   • Any vtable navigation fault / null pointer / wrong type → returns false.
//   • TryRead* methods never throw to caller; catch-all guards every code path.
//   • Do not Release IADLXSystem — call ADLXTerminate() only (on process exit
//     or explicit cleanup). All other interfaces are Released in finally blocks.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class AdlxNative : IAdlxNative
{
    // ADLX version 1.0.0.0 = (1UL << 48). Maximises driver compatibility.
    private const ulong AdlxVersion = 1UL << 48;

    // ── Flat C exports from amdadlx64.dll ────────────────────────────────

    [DllImport("amdadlx64", EntryPoint = "ADLXInitialize",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADLXInitialize(ulong version, out IntPtr ppSystem);

    [DllImport("amdadlx64", EntryPoint = "ADLXTerminate",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADLXTerminate();

    // ── Lazy init (thread-safe, double-checked) ───────────────────────────

    private readonly object _gate = new();
    private bool   _initAttempted;
    private bool   _initialized;
    private int    _initResult;   // raw ADLXInitialize return code (diagnostics)
    private string? _initDetail;  // human reason when init did not succeed (diagnostics)
    private IntPtr _pSystem; // IADLXSystem* — singleton; do not Release

    public AdlxNative() { }

    /// <summary>Tests : un IADLXSystem* déjà construit (tables doublures en mémoire non managée), sans
    /// appeler ADLXInitialize.</summary>
    internal AdlxNative(IntPtr systemDejaInitialise)
    {
        _initAttempted = true;
        _pSystem = systemDejaInitialise;
        _initialized = systemDejaInitialise != IntPtr.Zero;
        if (!_initialized) _initDetail = "IADLXSystem nul.";
    }

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _initialized;
        }
    }

    private void EnsureInitialized()
    {
        if (_initAttempted) return;
        lock (_gate)
        {
            if (_initAttempted) return;
            _initAttempted = true;
            try
            {
                int res = ADLXInitialize(AdlxVersion, out _pSystem);
                _initResult = res;
                // 0 = ADLX_OK  |  2 = ADLX_ALREADY_INITIALIZED — both are success.
                _initialized = (res == 0 || res == 2) && _pSystem != IntPtr.Zero;
                if (!_initialized)
                    _initDetail = $"ADLXInitialize returned {res}, pSystem={(_pSystem == IntPtr.Zero ? "null" : "set")}.";
            }
            catch (DllNotFoundException)
            {
                // AMD driver / amdadlx64.dll absent
                _initDetail = "amdadlx64.dll not found: AMD Adrenalin driver not installed (or not on the DLL search path).";
            }
            catch (Exception ex)
            {
                // any other init fault — stay unavailable
                _initDetail = $"ADLXInitialize threw {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    // ── Vtable helpers ────────────────────────────────────────────────────
    // Objects: first word = vtable*; vtable[slot] = function pointer.

    private static T? Slot<T>(IntPtr p, int slot) where T : Delegate
    {
        try
        {
            var vtbl = Marshal.ReadIntPtr(p);
            var fn   = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return fn == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(fn);
        }
        catch { return null; }
    }

    // Release any IADLXInterface-derived object (slot 1 for all except IADLXSystem).
    private static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { Slot<ReleaseDelegate>(p, AdlxVtableSlots.Release)?.Invoke(p); }
        catch { }
    }

    // ── TryReadGfx ────────────────────────────────────────────────────────

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            // GetGPUTuningServices (IADLXSystem slot 8)
            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualGFXTuning (slot 8)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var gfxOk) != 0 || gfxOk == 0)
                return false;

            // GetManualGFXTuning (slot 14) → IADLXManualGraphicsTuning1*
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pGfxBase) != 0
                || pGfxBase == IntPtr.Zero) return false;

            // Try QI for IADLXManualGraphicsTuning2 (RDNA); base is Tuning1 (slot 2 = QI).
            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi != null
                && qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) == 0
                && pGfxV2 != IntPtr.Zero)
            {
                return ReadGfxV2(pGfxV2, out reads);
            }

            // Vega/older path: Tuning1 does not expose the offset/voltage read APIs our
            // tuning uses. Report this honestly as "not readable" (false) rather than a
            // fake zero value — a 0 would let the gate pass on a card we cannot actually
            // offset-tune. The gate then skips cleanly (§22.3: never a false reading).
            return false;
        }
        catch { return false; }
        finally
        {
            if (pGfxV2     != IntPtr.Zero) Release(pGfxV2);
            if (pGfxBase   != IntPtr.Zero) Release(pGfxBase);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // Frequency is ESSENTIAL; voltage is OPTIONAL. RDNA4 (RX 9000) dropped manual
    // voltage control (like NVIDIA Blackwell) — GetGPUVoltage errors there while the
    // frequency reads succeed. Requiring voltage would make a perfectly tunable card
    // read as "unsupported". So: fail only if a FREQUENCY read fails; when voltage is
    // unavailable, default it to 0 / empty range and still return the freq data.
    private static bool ReadGfxV2(IntPtr pT, out AdlxGfxReads reads)
    {
        reads = default;
        try
        {
            var getMinRange  = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetMinFreqRange);
            var getMinFreq   = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetMinFreq);
            var getMaxRange  = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetMaxFreqRange);
            var getMaxFreq   = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetMaxFreq);
            var getVoltRange = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetVoltRange);
            var getVolt      = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetVolt);

            // Only the MAX-frequency reads are ESSENTIAL — that is the knob we tune.
            // RDNA4 (RX 9000) drops several others (min-frequency AND voltage), so treat
            // them ALL as OPTIONAL: a card that exposes MaxFreq + its range is tunable.
            if (getMaxRange == null || getMaxFreq == null) return false;
            AdlxIntRangeNative maxR = default; int maxFreq = 0;
            if (getMaxRange(pT, out maxR)   != 0) return false;
            if (getMaxFreq(pT, out maxFreq) != 0) return false;

            // Optional reads — default to 0 / empty when the driver does not expose them.
            AdlxIntRangeNative minR = default; int minFreq = 0;
            if (getMinRange != null && getMinRange(pT, out var mr2) == 0) minR = mr2;
            if (getMinFreq  != null && getMinFreq(pT,  out var mf2) == 0) minFreq = mf2;

            AdlxIntRangeNative voltR = default; int volt = 0;
            if (getVoltRange != null && getVolt != null
             && getVoltRange(pT, out var vr2) == 0 && getVolt(pT, out var v2) == 0)
            { voltR = vr2; volt = v2; }

            reads = new AdlxGfxReads
            {
                VoltMv       = volt,
                VoltRange    = ToPublic(voltR),
                MaxFreqMhz   = maxFreq,
                MaxFreqRange = ToPublic(maxR),
                MinFreqMhz   = minFreq,
                MinFreqRange = ToPublic(minR),
            };
            return true;
        }
        catch { return false; }
    }

    // ── TryReadPower ──────────────────────────────────────────────────────

    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList    = IntPtr.Zero;
        IntPtr pGpu        = IntPtr.Zero;
        IntPtr pTuningSvc  = IntPtr.Zero;
        IntPtr pPowerTune  = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualPowerTuning (slot 11)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualPower);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var pwrOk) != 0 || pwrOk == 0)
                return false;

            // GetManualPowerTuning (slot 17)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualPower);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pPowerTune) != 0
                || pPowerTune == IntPtr.Zero) return false;

            var getRange = Slot<GetRangeDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimitRange);
            var getPct   = Slot<GetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimit);
            if (getRange == null || getPct == null) return false;

            if (getRange(pPowerTune, out var pwrRange) != 0) return false;
            if (getPct(pPowerTune,   out var pct)      != 0) return false;

            reads = new AdlxPowerReads
            {
                PowerLimitPct = pct,
                PowerRange    = ToPublic(pwrRange),
            };
            return true;
        }
        catch { return false; }
        finally
        {
            if (pPowerTune != IntPtr.Zero) Release(pPowerTune);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── TryReadFan ────────────────────────────────────────────────────────

    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pFanTune   = IntPtr.Zero;
        IntPtr pStateList = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualFanTuning (slot 10)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualFan);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var fanOk) != 0 || fanOk == 0)
                return false;

            // GetManualFanTuning (slot 16)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualFan);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pFanTune) != 0
                || pFanTune == IntPtr.Zero) return false;

            // GetFanTuningRanges (slot 3): speedRange first, then tempRange
            var getRanges = Slot<GetTwoRangesDelegate>(pFanTune, AdlxVtableSlots.Fan_GetTuningRanges);
            if (getRanges == null) return false;
            if (getRanges(pFanTune, out var speedRange, out var tempRange) != 0)
                return false;

            // GetFanTuningStates (slot 4) → IADLXManualFanTuningStateList*
            var getStates = Slot<GetOutPtrDelegate>(pFanTune, AdlxVtableSlots.Fan_GetTuningStates);
            List<AdlxFanPoint> points = new();
            if (getStates != null
                && getStates(pFanTune, out pStateList) == 0
                && pStateList != IntPtr.Zero)
            {
                points = CollectFanPoints(pStateList);
            }

            reads = new AdlxFanReads
            {
                Points     = points,
                TempRange  = ToPublic(tempRange),
                SpeedRange = ToPublic(speedRange),
            };
            return true;
        }
        catch { return false; }
        finally
        {
            if (pStateList != IntPtr.Zero) Release(pStateList);
            if (pFanTune   != IntPtr.Zero) Release(pFanTune);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    private static List<AdlxFanPoint> CollectFanPoints(IntPtr pStateList)
    {
        var pts = new List<AdlxFanPoint>();
        try
        {
            var sizeFn  = Slot<SizeDelegate>(pStateList, AdlxVtableSlots.FanStateList_Size);
            var atState = Slot<AtIndexDelegate>(pStateList, AdlxVtableSlots.FanStateList_At);
            if (sizeFn == null || atState == null) return pts;

            uint count = sizeFn(pStateList);
            for (uint i = 0; i < count; i++)
            {
                IntPtr pState = IntPtr.Zero;
                try
                {
                    if (atState(pStateList, i, out pState) != 0 || pState == IntPtr.Zero)
                        continue;

                    var getSpeed = Slot<GetIntDelegate>(pState, AdlxVtableSlots.FanState_GetFanSpeed);
                    var getTemp  = Slot<GetIntDelegate>(pState, AdlxVtableSlots.FanState_GetTemperature);
                    if (getSpeed == null || getTemp == null) continue;

                    if (getSpeed(pState, out var speed) != 0) continue;
                    if (getTemp(pState,  out var temp)  != 0) continue;

                    pts.Add(new AdlxFanPoint { TempC = temp, SpeedPct = speed });
                }
                finally
                {
                    if (pState != IntPtr.Zero) Release(pState);
                }
            }
        }
        catch { /* return what we managed to collect */ }
        return pts;
    }

    // ── Shared: GetGPUs + At_GPUList ─────────────────────────────────────

    private bool GetGpuAtIndex(int gpuIndex, out IntPtr pGpuList, out IntPtr pGpu)
    {
        pGpuList = IntPtr.Zero;
        pGpu     = IntPtr.Zero;
        try
        {
            // IADLXSystem slot 1 = GetGPUs
            var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
            if (getGpus == null
                || getGpus(_pSystem, out pGpuList) != 0
                || pGpuList == IntPtr.Zero) return false;

            // IADLXGPUList slot 3 = Size
            var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
            if (sizeFn == null || gpuIndex >= (int)sizeFn(pGpuList)) return false;

            // IADLXGPUList slot 11 = At_GPUList
            var atFn = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
            if (atFn == null
                || atFn(pGpuList, (uint)gpuIndex, out pGpu) != 0
                || pGpu == IntPtr.Zero) return false;

            return true;
        }
        catch { return false; }
    }

    // ── Native struct mirrors ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlxIntRangeNative
    {
        public int minValue;
        public int maxValue;
        public int step;
    }

    private static AdlxIntRange ToPublic(AdlxIntRangeNative n) =>
        new() { Min = n.minValue, Max = n.maxValue, Step = n.step };

    // ── TrySetGfx ─────────────────────────────────────────────────────────

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        original = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualGFXTuning (slot 8) — only RDNA supports v2 + voltage
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var gfxOk) != 0 || gfxOk == 0)
                return false;

            // GetManualGFXTuning (slot 14)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pGfxBase) != 0
                || pGfxBase == IntPtr.Zero) return false;

            // QI for IADLXManualGraphicsTuning2 (required for voltage + set)
            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi == null
                || qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) != 0
                || pGfxV2 == IntPtr.Zero) return false;

            // GET current state first (get-then-set) — also captures ranges for validation
            if (!ReadGfxV2(pGfxV2, out original)) return false;

            // VALIDATE proposed values against hardware ranges before any write
            if (newMaxFreqMhz < original.MaxFreqRange.Min || newMaxFreqMhz > original.MaxFreqRange.Max)
                return false;
            if (newVoltMv < original.VoltRange.Min || newVoltMv > original.VoltRange.Max)
                return false;

            // SET MaxFreq (slot 8); Volt (slot 11) only if it actually changes
            return SetGfxV2(pGfxV2, newMaxFreqMhz, newVoltMv, original.MaxFreqMhz, original.VoltMv);
        }
        catch { return false; }
        finally
        {
            if (pGfxV2     != IntPtr.Zero) Release(pGfxV2);
            if (pGfxBase   != IntPtr.Zero) Release(pGfxBase);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    /// <summary>Sets MaxFreq (slot 8) and, only when it changes, Volt (slot 11) on a live
    /// IADLXManualGraphicsTuning2 pointer. The voltage write is skipped when
    /// <paramref name="newVoltMv"/> equals <paramref name="originalVoltMv"/> — RDNA4
    /// (RX 9000) drops manual voltage control, so SetGPUVoltage would fail there; since the
    /// harness never changes voltage (offset 0) we simply don't touch it. If SetVolt fails
    /// after SetMaxFreq already succeeded, best-effort rolls MaxFreq back to
    /// <paramref name="originalMaxFreqMhz"/>.</summary>
    private static bool SetGfxV2(IntPtr pT, int newMaxFreqMhz, int newVoltMv,
        int originalMaxFreqMhz, int originalVoltMv)
    {
        try
        {
            // SetGPUMaxFrequency — vtable slot 8 (IADLXManualGraphicsTuning1/2)
            var setMaxFreq = Slot<SetIntDelegate>(pT, AdlxVtableSlots.Gfx2_SetMaxFreq);
            if (setMaxFreq == null) return false;
            if (setMaxFreq(pT, newMaxFreqMhz) != 0) return false;

            // SetGPUVoltage — only when the voltage actually changes. Skipping a no-op
            // voltage write is both correct and what keeps RDNA4 (no manual voltage) working.
            if (newVoltMv != originalVoltMv)
            {
                var setVolt = Slot<SetIntDelegate>(pT, AdlxVtableSlots.Gfx2_SetVolt);
                if (setVolt == null || setVolt(pT, newVoltMv) != 0)
                {
                    // Partial-write rollback: best-effort restore MaxFreq
                    try { setMaxFreq(pT, originalMaxFreqMhz); } catch { }
                    return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    // ── TrySetPowerLimit ──────────────────────────────────────────────────────

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        original = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pPowerTune = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualPowerTuning (slot 11)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualPower);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var pwrOk) != 0 || pwrOk == 0)
                return false;

            // GetManualPowerTuning (slot 17)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualPower);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pPowerTune) != 0
                || pPowerTune == IntPtr.Zero) return false;

            // GET current state (get-then-set)
            var getRange = Slot<GetRangeDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimitRange);
            var getPct   = Slot<GetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimit);
            if (getRange == null || getPct == null) return false;
            if (getRange(pPowerTune, out var pwrRange) != 0) return false;
            if (getPct(pPowerTune,   out var currentPct) != 0) return false;

            original = new AdlxPowerReads
            {
                PowerLimitPct = currentPct,
                PowerRange    = ToPublic(pwrRange),
            };

            // VALIDATE before write
            if (newPowerLimitPct < original.PowerRange.Min || newPowerLimitPct > original.PowerRange.Max)
                return false;

            // SET power limit — vtable slot 5 (IADLXManualPowerTuning)
            var setPct = Slot<SetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_SetLimit);
            if (setPct == null) return false;
            return setPct(pPowerTune, newPowerLimitPct) == 0;
        }
        catch { return false; }
        finally
        {
            if (pPowerTune != IntPtr.Zero) Release(pPowerTune);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── ProbeGfxPath (diagnostics) ────────────────────────────────────────
    // Mirrors TryReadGfx's navigation but records the outcome of each stage so a
    // skipped gate reports exactly where ADLX stopped. Read-only; never throws.
    // Uses the same AdlxVtableSlots constants as the real path (no slot drift).

    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex)
    {
        var steps = new List<GpuProbeStep>();

        if (!IsAvailable)   // triggers EnsureInitialized → fills _initDetail on failure
        {
            steps.Add(new GpuProbeStep("adlx-init", false,
                _initDetail ?? "ADLXInitialize did not succeed (no AMD GPU / Adrenalin driver)."));
            return steps;
        }
        steps.Add(new GpuProbeStep("adlx-init", true, $"ADLXInitialize ok (result={_initResult})."));

        if (gpuIndex < 0)
        {
            steps.Add(new GpuProbeStep("gpu-index", false, $"Invalid GPU index {gpuIndex}."));
            return steps;
        }

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
            if (getGpus == null || getGpus(_pSystem, out pGpuList) != 0 || pGpuList == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-gpus", false, "IADLXSystem.GetGPUs returned no list."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-gpus", true));

            var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
            uint count = sizeFn?.Invoke(pGpuList) ?? 0u;
            if (sizeFn == null || gpuIndex >= (int)count)
            {
                steps.Add(new GpuProbeStep("gpu-count", false,
                    $"{count} GPU(s) listed; requested index {gpuIndex} is out of range."));
                return steps;
            }
            steps.Add(new GpuProbeStep("gpu-count", true, $"{count} GPU(s) listed; using index {gpuIndex}."));

            var atFn = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
            if (atFn == null || atFn(pGpuList, (uint)gpuIndex, out pGpu) != 0 || pGpu == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-gpu", false, "IADLXGPUList.At_GPUList returned no GPU."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-gpu", true));

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null || getTuningSvc(_pSystem, out pTuningSvc) != 0 || pTuningSvc == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("tuning-services", false, "GetGPUTuningServices failed."));
                return steps;
            }
            steps.Add(new GpuProbeStep("tuning-services", true));

            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null || isSupported(pTuningSvc, pGpu, out var gfxOk) != 0)
            {
                steps.Add(new GpuProbeStep("manual-gfx-supported", false, "IsSupportedManualGFXTuning call failed."));
                return steps;
            }
            if (gfxOk == 0)
            {
                steps.Add(new GpuProbeStep("manual-gfx-supported", false,
                    "Card reports manual GFX tuning UNSUPPORTED for this GPU/driver."));
                return steps;
            }
            steps.Add(new GpuProbeStep("manual-gfx-supported", true, "supported=yes"));

            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null || getManual(pTuningSvc, pGpu, out pGfxBase) != 0 || pGfxBase == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-manual-gfx", false, "GetManualGFXTuning returned no interface."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-manual-gfx", true));

            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi == null || qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) != 0 || pGfxV2 == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("qi-tuning2", false,
                    "No IADLXManualGraphicsTuning2: likely Vega/older; frequency/voltage offset tuning unavailable."));
                return steps;
            }
            steps.Add(new GpuProbeStep("qi-tuning2", true));

            // Granular reads — report which Get* fails. RDNA4 (RX 9000) keeps MAX-frequency
            // tuning but drops MIN-frequency and voltage; only MaxFreq + its range are required.
            var getMinRange  = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMinFreqRange);
            var getMinFreq   = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMinFreq);
            var getMaxRange  = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMaxFreqRange);
            var getMaxFreq   = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMaxFreq);
            var getVoltRange = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetVoltRange);
            var getVolt      = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetVolt);

            int tmpI = 0; AdlxIntRangeNative tmpR = default;
            bool maxFreqOk   = getMaxFreq   != null && getMaxFreq(pGfxV2,   out tmpI) == 0; int maxFreqVal = tmpI;
            bool maxRangeOk  = getMaxRange  != null && getMaxRange(pGfxV2,  out tmpR) == 0; AdlxIntRangeNative maxRangeVal = tmpR;
            bool minFreqOk   = getMinFreq   != null && getMinFreq(pGfxV2,   out tmpI) == 0;
            bool minRangeOk  = getMinRange  != null && getMinRange(pGfxV2,  out tmpR) == 0;
            bool voltOk      = getVolt      != null && getVolt(pGfxV2,      out tmpI) == 0;
            bool voltRangeOk = getVoltRange != null && getVoltRange(pGfxV2, out tmpR) == 0;

            steps.Add(new GpuProbeStep("read-maxfreq",       maxFreqOk,   maxFreqOk  ? $"offset={maxFreqVal} MHz" : "GetGPUMaxFrequency error"));
            steps.Add(new GpuProbeStep("read-maxfreq-range", maxRangeOk,  maxRangeOk ? $"range=[{maxRangeVal.minValue},{maxRangeVal.maxValue}] MHz" : "GetGPUMaxFrequencyRange error"));
            steps.Add(new GpuProbeStep("read-minfreq",       minFreqOk,   minFreqOk  ? "ok" : "GetGPUMinFrequency error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-minfreq-range", minRangeOk,  minRangeOk ? "ok" : "GetGPUMinFrequencyRange error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-voltage",       voltOk,      voltOk     ? "ok" : "GetGPUVoltage error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-voltage-range", voltRangeOk, voltRangeOk? "ok" : "GetGPUVoltageRange error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("gfx-tunable", maxFreqOk && maxRangeOk,
                (maxFreqOk && maxRangeOk)
                    ? "FREQUENCY tuning available (only MaxFreq required): this GPU is tunable"
                    : "MaxFreq read failed: cannot tune this GPU"));
            return steps;
        }
        catch (Exception ex)
        {
            steps.Add(new GpuProbeStep("probe-exception", false, $"{ex.GetType().Name}: {ex.Message}"));
            return steps;
        }
        finally
        {
            if (pGfxV2     != IntPtr.Zero) Release(pGfxV2);
            if (pGfxBase   != IntPtr.Zero) Release(pGfxBase);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── LireReglages (A1, lecture seule) ──────────────────────────────────
    // Chaque valeur est lue à part : une entrée qui rend une erreur ADLX laisse SA valeur à null et
    // n'empêche pas les autres. Aucune entrée Set* n'est appelée ici.

    private const int AdlxGpuTypeDiscrete = 2; // ADLX_GPU_TYPE.GPUTYPE_DISCRETE (ADLXDefines.h)

    public AdlxReglagesLus LireReglages()
    {
        if (!IsAvailable)
            return AdlxReglagesLus.RienLu(_initDetail ?? "ADLXInitialize n'a pas réussi.");

        IntPtr pGpuList = IntPtr.Zero;
        IntPtr pGpu     = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return AdlxReglagesLus.RienLu(raison);

            var (antiLag, frtcActif, frtcFps, frtcReglee, chillActif, chillMin, chillMax) = Lire3D(pGpu);
            var (vsyncMode, enhancedSync) = LireVsync(pGpu);
            return new AdlxReglagesLus(
                Sam: LireSam(pGpu),
                AntiLag: antiLag,
                FrtcActif: frtcActif,
                FrtcFps: frtcFps,
                ChillActif: chillActif,
                ChillMinFps: chillMin,
                ChillMaxFps: chillMax,
                TemperatureC: LireTemperature(pGpu),
                FrtcFpsReglee: frtcReglee,
                VsyncMode: vsyncMode,
                EnhancedSyncActif: enhancedSync);
        }
        catch (Exception ex) { return AdlxReglagesLus.RienLu($"Lecture ADLX interrompue : {ex.GetType().Name}."); }
        finally
        {
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // ── LireFreeSync (A-FS1, 08/10/2026, lecture seule) ─────────────────
    // IADLXSystem.GetDisplaysServices (3) → GetDisplays (4) → pour chaque écran : Name (6) et GetFreeSync (9), puis
    // IsSupported / IsEnabled. Le nom appartient à ADLX : il est copié, jamais libéré ici.
    public IReadOnlyList<AdlxEcranFreeSync> LireFreeSync()
    {
        var ecrans = new List<AdlxEcranFreeSync>();
        if (!IsAvailable) return ecrans;

        IntPtr pSvc = IntPtr.Zero, pListe = IntPtr.Zero;
        try
        {
            var getSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetDisplaysServices);
            if (getSvc == null || getSvc(_pSystem, out pSvc) != 0 || pSvc == IntPtr.Zero) return ecrans;
            var getListe = Slot<GetOutPtrDelegate>(pSvc, AdlxVtableSlots.DisplayServices_GetDisplays);
            if (getListe == null || getListe(pSvc, out pListe) != 0 || pListe == IntPtr.Zero) return ecrans;
            var taille = Slot<SizeDelegate>(pListe, AdlxVtableSlots.DisplayList_Size);
            var at = Slot<AtIndexDelegate>(pListe, AdlxVtableSlots.DisplayList_At);
            var getFs = Slot<GetManualDelegate>(pSvc, AdlxVtableSlots.DisplayServices_GetFreeSync);
            if (taille == null || at == null || getFs == null) return ecrans;

            for (uint i = 0, n = taille(pListe); i < n; i++)
            {
                IntPtr pEcran = IntPtr.Zero, pFs = IntPtr.Zero;
                try
                {
                    if (at(pListe, i, out pEcran) != 0 || pEcran == IntPtr.Zero) continue;
                    var getNom = Slot<GetOutPtrDelegate>(pEcran, AdlxVtableSlots.Display_Name);
                    var nom = getNom != null && getNom(pEcran, out var pNom) == 0 && pNom != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(pNom) : null;
                    var (sup, actif) = getFs(pSvc, pEcran, out pFs) == 0 && pFs != IntPtr.Zero
                        ? Etat(pFs) : ((bool?)null, (bool?)null);
                    ecrans.Add(new AdlxEcranFreeSync(nom ?? "", sup, actif));
                }
                finally
                {
                    Release(pFs);
                    Release(pEcran);
                }
            }
            return ecrans;
        }
        catch { return ecrans; }
        finally
        {
            Release(pListe);
            Release(pSvc);
        }
    }

    // ── EcrireAntiLag (A2, 07/10/2026) ──────────────────────────────────
    // Anti-Lag classique (IADLX3DAntiLag.SetEnabled, entrée 5 de I3DSettings.h), sur la même carte dédiée que
    // LireReglages. Le niveau (Anti-Lag Next, IADLX3DAntiLag1) n'est jamais touché. Rend null si écrit.
    public string? EcrireAntiLag(bool actif)
    {
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize n'a pas réussi.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pAntiLag = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetAntiLag);
            if (get == null || get(p3D, pGpu, out pAntiLag) != 0 || pAntiLag == IntPtr.Zero) return "IADLX3DAntiLag absent.";
            if (Etat(pAntiLag).Supporte != true) return "Anti-Lag n'est pas pris en charge par cette carte.";
            var set = Slot<SetBoolDelegate>(pAntiLag, AdlxVtableSlots.AntiLag_SetEnabled);
            if (set == null) return "IADLX3DAntiLag.SetEnabled introuvable.";
            var res = set(pAntiLag, actif ? (byte)1 : (byte)0);
            return res == 0 ? null : $"IADLX3DAntiLag.SetEnabled a rendu {res}.";
        }
        catch (Exception ex) { return $"Écriture ADLX interrompue : {ex.GetType().Name}."; }
        finally
        {
            Release(pAntiLag);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // ── EcrireFrtc (A2b, 07/10/2026) ────────────────────────────────────
    // IADLX3DFrameRateTargetControl (I3DSettings.h) : 5 GetFPSRange, 7 SetEnabled, 8 SetFPS. La cible d'abord,
    // dans la plage que le pilote donne, puis l'état. Même carte dédiée que LireReglages.
    public string? EcrireFrtc(int code)
    {
        if (code == 0) return "FRTC : 0 n'est ni une cible ni un état.";
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize n'a pas réussi.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pFrtc = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetFrameRateTargetControl);
            if (get == null || get(p3D, pGpu, out pFrtc) != 0 || pFrtc == IntPtr.Zero) return "IADLX3DFrameRateTargetControl absent.";
            if (Etat(pFrtc).Supporte != true) return "FRTC n'est pas pris en charge par cette carte.";

            var fps = Math.Abs(code);
            var plage = Slot<GetRangeDelegate>(pFrtc, AdlxVtableSlots.Frtc_GetFpsRange);
            if (plage == null || plage(pFrtc, out var r) != 0) return "FRTC : plage d'images illisible.";
            if (fps < r.minValue || fps > r.maxValue) return $"FRTC : {fps} images/s hors de la plage du pilote ({r.minValue}-{r.maxValue}).";

            var setFps = Slot<SetIntDelegate>(pFrtc, AdlxVtableSlots.Frtc_SetFps);
            var setOn = Slot<SetBoolDelegate>(pFrtc, AdlxVtableSlots.Frtc_SetEnabled);
            if (setFps == null || setOn == null) return "FRTC : entrées d'écriture introuvables.";
            var res = setFps(pFrtc, fps);
            if (res != 0) return $"IADLX3DFrameRateTargetControl.SetFPS a rendu {res}.";
            res = setOn(pFrtc, code > 0 ? (byte)1 : (byte)0);
            return res == 0 ? null : $"IADLX3DFrameRateTargetControl.SetEnabled a rendu {res}.";
        }
        catch (Exception ex) { return $"Écriture ADLX interrompue : {ex.GetType().Name}."; }
        finally
        {
            Release(pFrtc);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // La carte dédiée (IADLXGPU::Type == GPUTYPE_DISCRETE). Sur un Ryzen 7800X3D + RX 9070 XT, l'index 0 est
    // la puce intégrée (relevé du harnais, 29/06). Une seule carte : c'est elle. Plusieurs sans dédiée dite :
    // non lu, plutôt que lire la mauvaise. Rend null si trouvée, sinon la raison.
    private string? CarteDediee(out IntPtr pGpuList, out IntPtr pGpu)
    {
        pGpuList = IntPtr.Zero;
        pGpu     = IntPtr.Zero;
        var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
        if (getGpus == null || getGpus(_pSystem, out pGpuList) != 0 || pGpuList == IntPtr.Zero)
            return "IADLXSystem.GetGPUs n'a rendu aucune liste.";
        var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
        var atFn   = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
        if (sizeFn == null || atFn == null) return "IADLXGPUList illisible.";
        uint n = sizeFn(pGpuList);
        if (n == 0) return "ADLX ne liste aucune carte AMD.";

        for (uint i = 0; i < n; i++)
        {
            if (atFn(pGpuList, i, out var p) != 0 || p == IntPtr.Zero) continue;
            var type = Slot<GetIntDelegate>(p, AdlxVtableSlots.Gpu_Type);
            if (type != null && type(p, out var t) == 0 && t == AdlxGpuTypeDiscrete) { pGpu = p; return null; }
            Release(p);
        }
        if (n == 1 && atFn(pGpuList, 0, out var seule) == 0 && seule != IntPtr.Zero) { pGpu = seule; return null; }
        return $"ADLX liste {n} cartes et n'en dit aucune dédiée.";
    }

    // SAM : IADLXGPUTuningServices → QI IADLXGPUTuningServices1 → GetSmartAccessMemory → IsSupported / IsEnabled.
    // Non pris en charge = null : « la plateforme ne peut pas » n'est pas « éteint, à allumer ».
    private bool? LireSam(IntPtr pGpu)
    {
        IntPtr pTs = IntPtr.Zero, pTs1 = IntPtr.Zero, pSam = IntPtr.Zero;
        try
        {
            var getTs = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTs == null || getTs(_pSystem, out pTs) != 0 || pTs == IntPtr.Zero) return null;
            var qi = Slot<QueryIfDelegate>(pTs, AdlxVtableSlots.QueryInterface);
            if (qi == null || qi(pTs, "IADLXGPUTuningServices1", out pTs1) != 0 || pTs1 == IntPtr.Zero) return null;
            var getSam = Slot<GetManualDelegate>(pTs1, AdlxVtableSlots.Tuning1_GetSmartAccessMemory);
            if (getSam == null || getSam(pTs1, pGpu, out pSam) != 0 || pSam == IntPtr.Zero) return null;
            var (supporte, actif) = Etat(pSam);
            return supporte == true ? actif : null;
        }
        catch { return null; }
        finally { Release(pSam); Release(pTs1); Release(pTs); }
    }

    // Anti-Lag, Chill, FRTC : IADLX3DSettingsServices. Une fonction que la carte ne prend pas en charge est lue
    // éteinte (elle ne peut pas limiter les images ni agir).
    private (bool? AntiLag, bool? FrtcActif, int? FrtcFps, int? FrtcReglee, bool? ChillActif, int? ChillMin, int? ChillMax) Lire3D(IntPtr pGpu)
    {
        IntPtr p3D = IntPtr.Zero;
        try
        {
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return default;

            bool? antiLag = Fonction(p3D, AdlxVtableSlots.Settings3D_GetAntiLag, pGpu, _ => { });
            int? frtcFps = null;
            bool? frtc = Fonction(p3D, AdlxVtableSlots.Settings3D_GetFrameRateTargetControl, pGpu,
                p => frtcFps = Entier(p, AdlxVtableSlots.Frtc_GetFps));
            int? chillMin = null, chillMax = null;
            bool? chill = Fonction(p3D, AdlxVtableSlots.Settings3D_GetChill, pGpu, p =>
            {
                chillMin = Entier(p, AdlxVtableSlots.Chill_GetMinFps);
                chillMax = Entier(p, AdlxVtableSlots.Chill_GetMaxFps);
            });
            return (antiLag, frtc, frtc == true ? frtcFps : null, frtcFps, chill, chill == true ? chillMin : null, chill == true ? chillMax : null);
        }
        catch { return default; }
        finally { Release(p3D); }
    }

    // A2c : le mode Vsync du pilote et Enhanced Sync. Un mode non lu reste null, jamais « coupée ».
    private (int? Mode, bool? EnhancedSync) LireVsync(IntPtr pGpu)
    {
        IntPtr p3D = IntPtr.Zero;
        try
        {
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return default;
            int? mode = null;
            var vsync = Fonction(p3D, AdlxVtableSlots.Settings3D_GetWaitForVerticalRefresh, pGpu,
                p => mode = Entier(p, AdlxVtableSlots.Wfvr_GetMode));
            var enhanced = Fonction(p3D, AdlxVtableSlots.Settings3D_GetEnhancedSync, pGpu, _ => { });
            return (vsync is null ? null : mode, enhanced);
        }
        catch { return default; }
        finally { Release(p3D); }
    }

    // ── EcrireVsync (A2c, 08/10/2026) ───────────────────────────────────
    // IADLX3DWaitForVerticalRefresh.SetMode (entrée 6 de I3DSettings.h), même carte dédiée que LireReglages.
    public string? EcrireVsync(int mode)
    {
        if (mode is < 0 or > 3) return $"Vsync : mode {mode} inconnu (0 à 3).";
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize n'a pas réussi.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pVsync = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetWaitForVerticalRefresh);
            if (get == null || get(p3D, pGpu, out pVsync) != 0 || pVsync == IntPtr.Zero) return "IADLX3DWaitForVerticalRefresh absent.";
            if (Etat(pVsync).Supporte != true) return "La Vsync du pilote n'est pas prise en charge par cette carte.";
            var set = Slot<SetIntDelegate>(pVsync, AdlxVtableSlots.Wfvr_SetMode);
            if (set == null) return "IADLX3DWaitForVerticalRefresh.SetMode introuvable.";
            var res = set(pVsync, mode);
            return res == 0 ? null : $"IADLX3DWaitForVerticalRefresh.SetMode a rendu {res}.";
        }
        catch (Exception ex) { return $"Écriture ADLX interrompue : {ex.GetType().Name}."; }
        finally
        {
            Release(pVsync);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // Obtient l'interface d'une fonction 3D pour la carte, lit son état, et — prise en charge — ses valeurs
    // (A2b : aussi éteinte, pour rendre la cible FRTC exacte ; l'appelant ne garde que celles d'une fonction allumée).
    private static bool? Fonction(IntPtr p3D, int slot, IntPtr pGpu, Action<IntPtr> lireValeurs)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var get = Slot<GetManualDelegate>(p3D, slot);
            if (get == null || get(p3D, pGpu, out p) != 0 || p == IntPtr.Zero) return null;
            var (supporte, actif) = Etat(p);
            if (supporte == false) return false;
            if (supporte == true) lireValeurs(p);
            return actif;
        }
        catch { return null; }
        finally { Release(p); }
    }

    // IsSupported (slot 3) puis IsEnabled (slot 4), tête commune de SAM, Anti-Lag, Chill et FRTC.
    private static (bool? Supporte, bool? Actif) Etat(IntPtr p)
    {
        var sup = Slot<GetBoolDelegate>(p, AdlxVtableSlots.Feature_IsSupported);
        if (sup == null || sup(p, out var s) != 0) return (null, null);
        if (s == 0) return (false, null);
        var en = Slot<GetBoolDelegate>(p, AdlxVtableSlots.Feature_IsEnabled);
        if (en == null || en(p, out var e) != 0) return (true, null);
        return (true, e != 0);
    }

    private static int? Entier(IntPtr p, int slot)
    {
        var get = Slot<GetIntDelegate>(p, slot);
        return get != null && get(p, out var v) == 0 ? v : null;
    }

    // Température : IADLXPerformanceMonitoringServices.GetCurrentGPUMetrics → IADLXGPUMetrics.GPUTemperature.
    // Une valeur hors de (0, 150) °C n'est pas une température : non lue, surtout pas zéro.
    private double? LireTemperature(IntPtr pGpu)
    {
        IntPtr pPm = IntPtr.Zero, pMetrics = IntPtr.Zero;
        try
        {
            var getPm = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetPerformanceMonitoringServices);
            if (getPm == null || getPm(_pSystem, out pPm) != 0 || pPm == IntPtr.Zero) return null;
            var getMetrics = Slot<GetManualDelegate>(pPm, AdlxVtableSlots.Perf_GetCurrentGpuMetrics);
            if (getMetrics == null || getMetrics(pPm, pGpu, out pMetrics) != 0 || pMetrics == IntPtr.Zero) return null;
            var temp = Slot<GetDoubleDelegate>(pMetrics, AdlxVtableSlots.Metrics_GpuTemperature);
            return temp != null && temp(pMetrics, out var t) == 0 && t > 0 && t < 150 ? t : null;
        }
        catch { return null; }
        finally { Release(pMetrics); Release(pPm); }
    }

    // ── Delegates — all ADLX_STD_CALL (__stdcall on x64) ─────────────────

    // (pThis, out adlx_bool) — IsSupported / IsEnabled
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoolDelegate(IntPtr pThis, out sbyte val);

    // (pThis, out adlx_double) — IADLXGPUMetrics.GPUTemperature
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDoubleDelegate(IntPtr pThis, out double val);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long ReleaseDelegate(IntPtr pThis);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint SizeDelegate(IntPtr pThis);

    // (pThis, out ppOut) — used for GetGPUs, GetGPUTuningServices, GetFanTuningStates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutPtrDelegate(IntPtr pThis, out IntPtr ppOut);

    // (pThis, out val) — used for GetGPUVoltage, GetGPUMaxFrequency, GetPowerLimit, fan speed/temp
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIntDelegate(IntPtr pThis, out int val);

    // (pThis, out range) — single ADLX_IntRange out param
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRangeDelegate(IntPtr pThis, out AdlxIntRangeNative range);

    // (pThis, out speedRange, out tempRange) — GetFanTuningRanges
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTwoRangesDelegate(
        IntPtr pThis, out AdlxIntRangeNative r1, out AdlxIntRangeNative r2);

    // (pThis, uint idx, out ppItem) — used for At_GPUList and At_ManualFanTuningStateList
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AtIndexDelegate(IntPtr pThis, uint idx, out IntPtr ppItem);

    // (pThis, pGpu, out supported) — IsSupportedManualGFX/Fan/PowerTuning
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupportedDelegate(IntPtr pThis, IntPtr pGpu, out sbyte supported);

    // (pThis, pGpu, out ppIface) — GetManualGFX/Fan/PowerTuning
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetManualDelegate(IntPtr pThis, IntPtr pGpu, out IntPtr ppIface);

    // (pThis, interfaceId, out ppIface) — QueryInterface for v2 upgrade
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryIfDelegate(
        IntPtr pThis,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        out IntPtr ppIface);

    // (pThis, val) — used for SET operations: SetGPUMaxFrequency (slot 8),
    // SetGPUVoltage (slot 11), SetPowerLimit (slot 5)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetIntDelegate(IntPtr pThis, int val);

    // (pThis, adlx_bool) — IADLX3DAntiLag.SetEnabled ; adlx_bool est un octet (ADLXDefines.h).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoolDelegate(IntPtr pThis, byte val);
}
