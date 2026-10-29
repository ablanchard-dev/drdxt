using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiEntry : l'unique entrée dans nvapi64.dll, qui n'exporte que nvapi_QueryInterface(uint id) → un pointeur de
// fonction, puis GetDelegateForFunctionPointer (recette « Entry mechanism » + « Function IDs »). Sans NVIDIA, la DLL
// manque et le DllImport lève DllNotFoundException : emballé ici, IsAvailable vaut false et GetDelegate<T> rend null,
// pour un Result.Fail propre chez l'appelant, jamais d'exception à travers l'IPC.
//
// Règles d'or de sécurité respectées par les appelants de cette couche :
//   1. mot de version = Marshal.SizeOf(struct) | (verNum << 16), calculé à l'exécution
//      (MakeVersion ci-dessous), jamais codé en dur.
//   2. Pack = 8 sur chaque struct d'interop ; préallouer chaque ByValArray.
//   3. statut 0 = Ok ; tout non nul → échec, jamais de suite.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
internal sealed class NvApiEntry
{
    // Ids de fonction (table de la recette), recoupés avec le nvapi_interface.h public de NVIDIA là où c'est marqué.
    private const uint Id_Initialize = 0x0150E828;
    private const uint Id_EnumPhysicalGPUs = 0xE5AC921F;

    // QueryInterface(id) → pointeur de fonction. Cdecl, la convention d'export de nvapi64.
    [DllImport("nvapi64", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nvapi_QueryInterface(uint id);

    private readonly object _gate = new();
    // Levé seulement une fois l'init finie : un appelant concurrent attend le verrou au lieu de lire « indisponible ».
    private volatile bool _initAttempted;
    private bool _initialized;
    private readonly Func<int>? _initialiser;

    public NvApiEntry() { }

    /// <summary>Tests : remplace l'appel à NvAPI_Initialize.</summary>
    internal NvApiEntry(Func<int> initialiser) => _initialiser = initialiser;

    /// <summary>MAKE_NVAPI_VERSION : taille de la struct (16 bits bas) et version (16 hauts), calculée à l'exécution : un
    /// écart C#/C remonte en IncompatibleStructVersion du pilote, jamais une écriture corrompue (règle d'or n°1).</summary>
    public static uint MakeVersion<T>(uint versionNumber) where T : struct =>
        (uint)Marshal.SizeOf<T>() | (versionNumber << 16);

    /// <summary>Vrai une fois NvAPI_Initialize Ok, tenté à la première requête ; DLL absente ou statut non Ok : false.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _initialized;
        }
    }

    /// <summary>Un délégué NvAPI typé par id de fonction ; null sans nvapi64 ou pour un id non exporté (IntPtr.Zero). Ne
    /// lève jamais.</summary>
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
            return null; // machine AMD / sans NVIDIA : la bibliothèque n'existe pas.
        }
        catch (Exception)
        {
            return null; // tout accroc d'interop → indisponible, jamais de plantage.
        }
    }

    // --- NvAPI_Initialize ---
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    /// <summary>NvAPI_Initialize une seule fois, idempotent et thread-safe.</summary>
    public void EnsureInitialized()
    {
        if (_initAttempted)
            return;
        lock (_gate)
        {
            if (_initAttempted)
                return;
            try
            {
                var init = _initialiser;
                if (init is null && GetDelegate<InitializeDelegate>(Id_Initialize) is { } natif)
                    init = () => natif();
                // nvapi64 absent / Initialize non exporté → indisponible.
                _initialized = init is not null && init() == (int)NvApiStatus.Ok;
            }
            catch (Exception)
            {
                _initialized = false; // échec propre : ne jamais lever hors de l'init.
            }
            _initAttempted = true;
        }
    }

    // --- NvAPI_EnumPhysicalGPUs(NvPhysicalGpuHandle[] handles, out int count) ---
    // NVAPI_MAX_PHYSICAL_GPUS (64) pointeurs opaques, préalloués en entier (règle d'or n°2).
    private const int MaxPhysicalGpus = 64;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate(
        [Out] IntPtr[] handles, out int count);

    /// <summary>Les handles des GPU NVIDIA physiques ; vide sur tout échec ou sans NVIDIA. Ne lève jamais.</summary>
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

            var handles = new IntPtr[MaxPhysicalGpus]; // préalloué à SizeConst.
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
