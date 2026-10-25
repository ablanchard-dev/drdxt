using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiEntry : l'unique mécanisme d'entrée dans nvapi64.dll.
//
// nvapi64.dll n'exporte que nvapi_QueryInterface(uint id) → un pointeur de fonction. Tout le reste
// s'atteint par QueryInterface(id) → GetDelegateForFunctionPointer. (recette « Entry mechanism » +
// « Function IDs ».)
//
// Contrat d'échec propre : sur une machine AMD / sans NVIDIA, nvapi64.dll est absente et le
// DllImport lève DllNotFoundException au premier appel. Tout est emballé ici :
//   * IsAvailable vaut false (la bibliothèque n'a pas pu se charger / Initialize a échoué),
//   * GetDelegate<T> rend null,
// pour qu'un appelant change « indisponible » en Result.Fail propre : jamais de plantage, jamais
// d'exception à travers la frontière IPC.
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
    // Ids de fonction (table de la recette) : valeurs exactes, recoupées avec le nvapi_interface.h
    // public de NVIDIA là où c'est marqué.
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

    /// <summary>MAKE_NVAPI_VERSION : taille de la struct dans les 16 bits bas, numéro de version
    /// dans les 16 hauts. Calculé à l'exécution depuis la taille marshalée de la struct, pour qu'un
    /// écart de taille C#/C remonte comme l'IncompatibleStructVersion sûr du pilote, jamais une
    /// écriture corrompue. (Règle d'or n°1.)</summary>
    public static uint MakeVersion<T>(uint versionNumber) where T : struct =>
        (uint)Marshal.SizeOf<T>() | (versionNumber << 16);

    /// <summary>Vrai une fois que NvAPI_Initialize a rendu Ok. Tente l'init paresseusement à la
    /// première requête ; un nvapi64.dll absent ou un statut non Ok le laisse à false.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _initialized;
        }
    }

    /// <summary>Résout un délégué NvAPI typé par id de fonction. Rend null quand nvapi64 est
    /// indisponible ou que l'id n'est pas exporté par le pilote installé (les points d'entrée privés /
    /// non pris en charge rendent IntPtr.Zero). Ne lève jamais.</summary>
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

    /// <summary>Appelle paresseusement NvAPI_Initialize exactement une fois. Idempotent et
    /// thread-safe. Une bibliothèque absente ou un statut non Ok laisse l'entrée « indisponible ».</summary>
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
    // Le tableau de handles est un tableau fixe de NVAPI_MAX_PHYSICAL_GPUS (64) pointeurs opaques ;
    // préalloué à sa pleine taille (règle d'or n°2).
    private const int MaxPhysicalGpus = 64;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate(
        [Out] IntPtr[] handles, out int count);

    /// <summary>Énumère les handles des GPU NVIDIA physiques. Rend un tableau vide (statut autre que
    /// Ok) sur tout échec / sur une machine sans NVIDIA : ne lève jamais.</summary>
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
