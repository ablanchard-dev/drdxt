using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.Igcl;

// -----------------------------------------------------------------------
// 07/10/2026 (A5) : la température d'une carte Intel par IGCL (Intel Graphics Control Library), en lecture seule.
//
// Source : intel/drivers.gpu.control-library, include/igcl_api.h et Source/cApiWrapper.cpp (licence MIT), lus le
// 07/10/2026. ControlLib.dll est livrée par le pilote Intel ; le chargeur officiel la cherche dans System32 seulement
// (LoadLibraryExW + LOAD_LIBRARY_SEARCH_SYSTEM32), comme ici. Les fonctions sont exportées par leur nom (GetProcAddress).
//
//   ctlInit(ctl_init_args_t*, ctl_api_handle_t*)          AppVersion = CTL_MAKE_VERSION(1, 1), drapeau USE_LEVEL_ZERO
//   ctlEnumerateDevices(api, uint32_t*, handle*)            deux appels : le nombre, puis les poignées
//   ctlEnumTemperatureSensors(carte, uint32_t*, handle*)    idem
//   ctlTemperatureGetProperties(sonde, ctl_temp_properties_t*)   type de sonde (GPU, mémoire, global, minima)
//   ctlTemperatureGetState(sonde, double*)                   degrés Celsius
//
// Non prouvé sur une vraie carte Intel (aucune ici) : la disposition des deux structures est figée par un test de taille
// contre l'en-tête ; la valeur rendue, elle, attend une machine Arc.
// ctlInit une fois (paresseux), jamais ctlClose : comme NVML, la poignée meurt avec le processus.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class IgclNative
{
    public const string Dll = "ControlLib.dll";
    public const string DllAbsente = "ControlLib.dll absente de System32 : pas de pilote graphique Intel récent sur ce PC.";

    private const int Succes = 0;              // CTL_RESULT_SUCCESS
    private const uint DrapeauLevelZero = 1;   // CTL_INIT_FLAG_USE_LEVEL_ZERO : « requis pour la télémétrie »
    internal const uint Version11 = (1u << 16) | 1u; // CTL_MAKE_VERSION(CTL_IMPL_MAJOR_VERSION, CTL_IMPL_MINOR_VERSION)
    internal const int SondeGlobale = 0;       // CTL_TEMP_SENSORS_GLOBAL : le maximum de toutes les sondes de la carte
    internal const int SondeGpu = 1;           // CTL_TEMP_SENSORS_GPU : le maximum des sondes de la puce graphique

    public static bool DllPresente()
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, Dll)); }
        catch (Exception) { return false; }
    }

    private readonly Lazy<(IntPtr Api, string? Raison)> _init;
    private EnumDelegate? _cartes, _sondes;
    private ProprietesDelegate? _proprietes;
    private EtatDelegate? _etat;

    public IgclNative() => _init = new(Initialiser, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>La température de la puce graphique la plus chaude, ou null et la raison. Ne lève jamais.</summary>
    public (double? Celsius, string? Raison) LireTemperature()
    {
        try
        {
            var (api, raison) = _init.Value;
            if (raison is not null) return (null, raison);
            var lues = new List<(int, double)>();
            foreach (var carte in Poignees(_cartes!, api))
                foreach (var sonde in Poignees(_sondes!, carte))
                {
                    var p = new CtlTempProperties { Size = (uint)Marshal.SizeOf<CtlTempProperties>() };
                    if (_proprietes!(sonde, ref p) != Succes || _etat!(sonde, out var c) != Succes) continue;
                    lues.Add((p.Type, c));
                }
            return Choisir(lues) is { } t ? (t, null) : (null, "le pilote Intel n'a rendu aucune température de puce graphique");
        }
        catch (Exception ex) { return (null, $"lecture IGCL interrompue : {ex.GetType().Name}"); }
    }

    /// <summary>La sonde « GPU » d'abord (le maximum de la puce), la sonde globale à défaut ; la mémoire et les minima
    /// ne sont pas la chaleur de la puce. Plusieurs cartes Intel (puce intégrée et Arc) : la plus chaude. Hors de
    /// ]0, 150[ °C, une valeur n'est pas une température (même garde qu'ADLX).</summary>
    internal static double? Choisir(IEnumerable<(int Type, double Celsius)> lues)
    {
        var valides = lues.Where(l => l.Celsius is > 0 and < 150 && double.IsFinite(l.Celsius)).ToList();
        foreach (var type in new[] { SondeGpu, SondeGlobale })
            if (valides.Where(l => l.Type == type).Select(l => l.Celsius).DefaultIfEmpty().Max() is var m and > 0) return m;
        return null;
    }

    private static List<IntPtr> Poignees(EnumDelegate enumerer, IntPtr parent)
    {
        uint n = 0;
        if (enumerer(parent, ref n, null) != Succes || n == 0) return [];
        var t = new IntPtr[n];
        return enumerer(parent, ref n, t) == Succes ? t.Take((int)Math.Min(n, (uint)t.Length)).ToList() : [];
    }

    private (IntPtr, string?) Initialiser()
    {
        if (!DllPresente()) return (IntPtr.Zero, DllAbsente);
        if (!NativeLibrary.TryLoad(Dll, typeof(IgclNative).Assembly, DllImportSearchPath.System32, out var lib))
            return (IntPtr.Zero, "ControlLib.dll présente mais non chargée");
        var init = Resoudre<InitDelegate>(lib, "ctlInit");
        _cartes = Resoudre<EnumDelegate>(lib, "ctlEnumerateDevices");
        _sondes = Resoudre<EnumDelegate>(lib, "ctlEnumTemperatureSensors");
        _proprietes = Resoudre<ProprietesDelegate>(lib, "ctlTemperatureGetProperties");
        _etat = Resoudre<EtatDelegate>(lib, "ctlTemperatureGetState");
        if (init is null || _cartes is null || _sondes is null || _proprietes is null || _etat is null)
            return (IntPtr.Zero, "ControlLib.dll n'expose pas les fonctions de température");
        var args = new CtlInitArgs { Size = (uint)Marshal.SizeOf<CtlInitArgs>(), AppVersion = Version11, Flags = DrapeauLevelZero };
        var r = init(ref args, out var api);
        return r == Succes && api != IntPtr.Zero ? (api, null) : (IntPtr.Zero, $"ctlInit a échoué (0x{r:X8})");
    }

    private static T? Resoudre<T>(IntPtr lib, string nom) where T : Delegate
        => NativeLibrary.TryGetExport(lib, nom, out var p) && p != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    // ctl_init_args_t : uint32 Size ; uint8 Version ; ctl_version_info_t AppVersion ; ctl_init_flags_t flags ;
    // ctl_version_info_t SupportedVersion ; ctl_application_id_t ApplicationUID (uint32, uint16, uint16, uint8[8]).
    [StructLayout(LayoutKind.Sequential)]
    internal struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    // ctl_temp_properties_t : uint32 Size ; uint8 Version ; ctl_temp_sensors_t type ; double maxTemperature.
    [StructLayout(LayoutKind.Sequential)]
    internal struct CtlTempProperties
    {
        public uint Size;
        public byte Version;
        public int Type;
        public double MaxTemperature;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(ref CtlInitArgs args, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumDelegate(IntPtr parent, ref uint count, [Out] IntPtr[]? handles);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProprietesDelegate(IntPtr sonde, ref CtlTempProperties proprietes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EtatDelegate(IntPtr sonde, out double celsius);
}
