using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// Pour tout l'assembly. Le helper eleve charge nvapi64, amdadlx64 et nvml par leur
// nom ; sur une carte de l'autre marque la DLL manque dans System32, et la recherche par defaut
// continuerait dans le PATH (dossiers modifiables par l'utilisateur). Les pilotes installent ces
// DLL dans System32 : on ne cherche que la.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxNative : l'unique vraie frontière P/Invoke vers amdadlx64.dll.
//
// Architecture
// ────────────
// ADLX utilise une ABI C proche de COM : chaque objet est une struct dont le premier champ est un
// pointeur vers sa vtable (un tableau de pointeurs de fonction). On parcourt la vtable via
// Marshal.ReadIntPtr, sans bloc unsafe.
//
// Séquence d'initialisation
// ─────────────
//   ADLXInitialize(1.0.0 version) → IADLXSystem* (jamais de Release dessus)
//   IADLXSystem::GetGPUs         (slot 1) → IADLXGPUList*
//   IADLXSystem::GetGPUTuningServices (slot 8) → IADLXGPUTuningServices*
//   [per GPU]
//     IsSupportedManualGFXTuning / IsSupportedManualPowerTuning / IsSupportedManualFanTuning
//     GetManualGFXTuning → IADLXManualGraphicsTuning1* → QI → IADLXManualGraphicsTuning2*
//     GetManualPowerTuning → IADLXManualPowerTuning*
//     GetManualFanTuning   → IADLXManualFanTuning*
//
// Emplacements de vtable : les constantes nommées de AdlxVtableSlots.cs, la citation par emplacement dans ADLX-SLOTS.md,
// vérifiés le 2026-06-29 contre les en-têtes du SDK ADLX (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include/*.h).
// Tuning1 ne sert qu'à obtenir Tuning2 par QueryInterface (emplacement 2) ; ses emplacements 3-7 ne sont jamais appelés.
//
// Contrat d'échec propre
// ───────────────────
//   • DllNotFoundException (DLL absente, pas de pilote AMD) → IsAvailable = false.
//   • Toute panne de parcours de vtable / pointeur null / mauvais type → rend false.
//   • Les méthodes TryRead* ne lèvent jamais vers l'appelant ; un catch-all garde chaque chemin.
//   • Pas de Release sur IADLXSystem : le singleton vit jusqu'à la sortie du processus, qui le libère (ADLXTerminate
//     est déclarée, jamais appelée). Toutes les autres interfaces sont libérées dans des finally.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed partial class AdlxNative : IAdlxNative
{
    // Version ADLX 1.0.0.0 = (1UL << 48). Maximise la compatibilité des pilotes.
    private const ulong AdlxVersion = 1UL << 48;

    // ── Exports C plats d'amdadlx64.dll ────────────────────────────────

    [DllImport("amdadlx64", EntryPoint = "ADLXInitialize",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADLXInitialize(ulong version, out IntPtr ppSystem);

    [DllImport("amdadlx64", EntryPoint = "ADLXTerminate",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADLXTerminate();

    // ── Init paresseuse (thread-safe, double vérification) ───────────────────────────

    private readonly object _gate = new();
    // Levé seulement une fois l'init finie : un appelant concurrent attend le verrou au lieu de lire « indisponible ».
    private volatile bool _initAttempted;
    private bool   _initialized;
    private int    _initResult;   // code de retour brut d'ADLXInitialize (diagnostics)
    private string? _initDetail;  // raison lisible quand l'init n'a pas réussi (diagnostics)
    private IntPtr _pSystem; // IADLXSystem* : singleton ; pas de Release

    private readonly Func<(int Res, IntPtr Systeme)>? _initialiser;

    public AdlxNative() { }

    /// <summary>Tests : remplace l'appel à ADLXInitialize.</summary>
    internal AdlxNative(Func<(int Res, IntPtr Systeme)> initialiser) => _initialiser = initialiser;

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
            try
            {
                int res;
                if (_initialiser is null) res = ADLXInitialize(AdlxVersion, out _pSystem);
                else (res, _pSystem) = _initialiser();
                _initResult = res;
                // 0 = ADLX_OK  |  2 = ADLX_ALREADY_INITIALIZED : les deux sont un succès.
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
                // toute autre panne d'init : rester indisponible
                _initDetail = $"ADLXInitialize threw {ex.GetType().Name}: {ex.Message}";
            }
            _initAttempted = true;
        }
    }

    // ── Vtable helpers ────────────────────────────────────────────────────
    // Objets : premier mot = vtable* ; vtable[slot] = pointeur de fonction.

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

    // Libérer tout objet dérivé d'IADLXInterface (emplacement 1 pour tous sauf IADLXSystem).
    private static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { Slot<ReleaseDelegate>(p, AdlxVtableSlots.Release)?.Invoke(p); }
        catch { /* libération au mieux : une référence perdue ne fait pas échouer la lecture */ }
    }

    // ── Délégués : tous ADLX_STD_CALL (__stdcall sur x64) ─────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long ReleaseDelegate(IntPtr pThis);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint SizeDelegate(IntPtr pThis);

    // (pThis, out ppOut) : sert à GetGPUs, GetGPUTuningServices, GetFanTuningStates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutPtrDelegate(IntPtr pThis, out IntPtr ppOut);

    // (pThis, out val) : sert à GetGPUVoltage, GetGPUMaxFrequency, GetPowerLimit, vitesse / température du ventilateur
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIntDelegate(IntPtr pThis, out int val);

    // (pThis, out range) : un seul paramètre de sortie ADLX_IntRange
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRangeDelegate(IntPtr pThis, out AdlxIntRangeNative range);

    // (pThis, uint idx, out ppItem) : sert à At_GPUList et At_ManualFanTuningStateList
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AtIndexDelegate(IntPtr pThis, uint idx, out IntPtr ppItem);

    // (pThis, pGpu, out supported) : IsSupportedManualGFX/Fan/PowerTuning
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupportedDelegate(IntPtr pThis, IntPtr pGpu, out sbyte supported);

    // (pThis, pGpu, out ppIface) : GetManualGFX/Fan/PowerTuning
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetManualDelegate(IntPtr pThis, IntPtr pGpu, out IntPtr ppIface);

    // (pThis, interfaceId, out ppIface) : QueryInterface pour passer à la v2
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryIfDelegate(
        IntPtr pThis,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        out IntPtr ppIface);
}
