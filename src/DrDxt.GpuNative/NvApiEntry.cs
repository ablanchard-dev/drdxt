using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiEntry — the SINGLE entry mechanism into nvapi64.dll.
//
// nvapi64.dll exports only nvapi_QueryInterface(uint id) → a function pointer.
// Everything else is reached by QueryInterface(id) → GetDelegateForFunctionPointer.
// (recipe "Entry mechanism" + "Function IDs".)
//
// Fail-clean contract: on an AMD / no-NVIDIA box nvapi64.dll is absent and the
// DllImport throws DllNotFoundException on first call. EVERYTHING here is wrapped:
//   * IsAvailable is false (the library could not be loaded / Initialize failed),
//   * GetDelegate<T> returns null,
// so a caller turns "unavailable" into a clean Result.Fail — never a crash, never
// a throw across the IPC boundary.
//
// Golden safety rules honoured by callers of this layer:
//   1. version word = Marshal.SizeOf(struct) | (verNum << 16), computed at runtime
//      (MakeVersion below) — never hard-coded.
//   2. Pack = 8 on every interop struct; pre-allocate every ByValArray.
//   3. status 0 = Ok; anything non-zero → fail, never proceed.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
internal sealed class NvApiEntry
{
    // Function IDs (recipe table) — exact values, cross-checked vs NVIDIA's public
    // nvapi_interface.h where marked .
    private const uint Id_Initialize = 0x0150E828;
    private const uint Id_EnumPhysicalGPUs = 0xE5AC921F;

    // QueryInterface(id) → fn pointer. Cdecl, the nvapi64 export convention.
    [DllImport("nvapi64", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nvapi_QueryInterface(uint id);

    private readonly object _gate = new();
    private bool _initAttempted;
    private bool _initialized;

    /// <summary>MAKE_NVAPI_VERSION: struct size in the low 16 bits, version number in
    /// the high 16. Computed at runtime from the marshalled struct size so a C#/C
    /// size mismatch surfaces as the driver's SAFE IncompatibleStructVersion, never
    /// a corrupt write. (Golden rule #1.)</summary>
    public static uint MakeVersion<T>(uint versionNumber) where T : struct =>
        (uint)Marshal.SizeOf<T>() | (versionNumber << 16);

    /// <summary>True once NvAPI_Initialize has returned Ok. Lazily attempts init on
    /// first query; a missing nvapi64.dll or a non-Ok status leaves it false.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _initialized;
        }
    }

    /// <summary>Resolves a typed NvAPI delegate by function id. Returns null when
    /// nvapi64 is unavailable or the id is not exported by the installed driver
    /// (private/unsupported entry points return IntPtr.Zero). Never throws.</summary>
    public T? GetDelegate<T>(uint id) where T : Delegate
    {
        try
        {
            var ptr = nvapi_QueryInterface(id);
            if (ptr == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
        catch (DllNotFoundException)
        {
            return null; // AMD / no-NVIDIA box: the library does not exist.
        }
        catch (Exception)
        {
            return null; // any interop hiccup → unavailable, never a crash.
        }
    }

    // --- NvAPI_Initialize ---
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    /// <summary>Lazily calls NvAPI_Initialize exactly once. Idempotent + thread-safe.
    /// A missing library or non-Ok status leaves the entry "unavailable".</summary>
    public void EnsureInitialized()
    {
        if (_initAttempted)
            return;
        lock (_gate)
        {
            if (_initAttempted)
                return;
            _initAttempted = true;
            try
            {
                var init = GetDelegate<InitializeDelegate>(Id_Initialize);
                if (init is null)
                    return; // nvapi64 absent / Initialize not exported → unavailable.
                _initialized = init() == (int)NvApiStatus.Ok;
            }
            catch (Exception)
            {
                _initialized = false; // fail-clean: never throw out of init.
            }
        }
    }

    // --- NvAPI_EnumPhysicalGPUs(NvPhysicalGpuHandle[] handles, out int count) ---
    // The handle array is a fixed NVAPI_MAX_PHYSICAL_GPUS (64) array of opaque
    // pointers; pre-allocated to its full size (golden rule #2).
    private const int MaxPhysicalGpus = 64;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate(
        [Out] IntPtr[] handles, out int count);

    /// <summary>Enumerates the physical NVIDIA GPU handles. Returns an empty array
    /// (status anything but Ok) on any failure / no-NVIDIA box — never throws.</summary>
    public IReadOnlyList<IntPtr> EnumPhysicalGpus()
    {
        EnsureInitialized();
        if (!_initialized)
            return Array.Empty<IntPtr>();

        try
        {
            var enumerate = GetDelegate<EnumPhysicalGpusDelegate>(Id_EnumPhysicalGPUs);
            if (enumerate is null)
                return Array.Empty<IntPtr>();

            var handles = new IntPtr[MaxPhysicalGpus]; // pre-allocated to SizeConst.
            var status = enumerate(handles, out var count);
            if (status != (int)NvApiStatus.Ok || count <= 0)
                return Array.Empty<IntPtr>();

            count = Math.Clamp(count, 0, MaxPhysicalGpus);
            return handles.Take(count).Where(h => h != IntPtr.Zero).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<IntPtr>();
        }
    }
}
