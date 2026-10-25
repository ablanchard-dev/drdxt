using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.Nvml;

// -----------------------------------------------------------------------
// NvmlNative : l'unique vraie frontière P/Invoke vers nvml.dll.
//
// Contrairement à NvApiNative (nvapi_QueryInterface + ids privés rétro-ingéniés), NVML exporte ses
// fonctions par leur nom : on les résout directement sur le module chargé. On charge le module
// soi-même (NativeLibrary.TryLoad) plutôt que par un [DllImport] statique, pour qu'une machine sans
// pilote NVIDIA (nvml.dll absente) donne IsAvailable == false au lieu d'une
// DllNotFoundException : le contrat d'échec propre.
//
// Surface : nvmlInit_v2 → nvmlDeviceGetHandleByIndex_v2, puis en lecture (non élevée) mémoire et BAR1, puissance,
// température, horloges, raisons de bridage, lien PCIe, processus graphiques, version du pilote et ventilateurs ; en
// écriture (helper élevé seulement) la vitesse des ventilateurs et leur retour à l'automatique.
//
// Symboles et structs exacts figés contre nvml.h (NVML_SUCCESS = 0 ; nvmlDevice_t = pointeur
// opaque ; nvmlMemory_t { ull total, free, used } ; nvmlBAR1Memory_t { ull bar1Total, bar1Free,
// bar1Used }). Sur x64 il n'y a qu'une convention d'appel native : l'attribut Cdecl ne change rien
// mais est gardé par symétrie avec NvApiNative.
//
// nvmlInit est appelé une fois (paresseux, thread-safe) et exprès jamais arrêté : l'init de NVML
// est comptée par référence et nos lectures sont brèves ; l'OS récupère le handle à la sortie du processus.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvmlNative : INvmlNative
{
    private const int NvmlSuccess = 0; // NVML_SUCCESS (nvml.h)
    private const int NvmlTemperatureGpu = 0; // nvmlTemperatureSensors_t NVML_TEMPERATURE_GPU (nvml.h)
    private const int NvmlTotalPowerSamples = 0; // nvmlSamplingType_t NVML_TOTAL_POWER_SAMPLES (nvml.h)
    private const int NvmlDriverVersionBufferSize = 80; // NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE (nvml.h)
    private const int NvmlClockGraphics = 0; // nvmlClockType_t NVML_CLOCK_GRAPHICS (nvml.h)
    private const int NvmlClockMem = 2;      // nvmlClockType_t NVML_CLOCK_MEM (nvml.h)

    // Emplacement de repli des anciens paquets de pilote (les pilotes récents mettent nvml.dll dans System32,
    // déjà couvert par la recherche par défaut). Le vrai Program Files, jamais un C:\ codé en dur : avec Windows sur un autre
    // disque, C:\ peut être un volume de données où n'importe quel compte crée des dossiers, et ce repli se charge aussi dans le helper élevé.
    internal static readonly string NvsmiFallback =
        CheminDeSecours(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    internal static string CheminDeSecours(string programFiles) =>
        Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");

    // Un drapeau « déjà tenté » posé avant la fin de nvmlInit laisserait un second fil
    // sortir tout de suite et lire « NVML absent » : le Resizable BAR sortirait « non lu » un scan sur
    // deux. Lazy (ExecutionAndPublication) fait attendre chaque appelant jusqu'à la vraie réponse.
    private readonly Lazy<bool> _init;
    private bool _available => _init.Value;

    public NvmlNative() => _init = new Lazy<bool>(Initialiser, LazyThreadSafetyMode.ExecutionAndPublication);

    private HandleByIndexDelegate? _getHandle;
    private MemoryInfoDelegate? _getMemory;
    private Bar1MemoryInfoDelegate? _getBar1;
    private PowerUsageDelegate? _getPower;
    private TemperatureDelegate? _getTemp;
    private UintValueDelegate? _pcieGen, _pcieLargeur, _pcieGenMax, _pcieLargeurMax;
    private ClockInfoDelegate? _getMaxClock;
    private ProcessesDelegate? _processusGraphiques;
    private ClockInfoDelegate? _getClock;
    private ClocksEventReasonsDelegate? _getClocksEventReasons;
    private SamplesDelegate? _getSamples;
    private SystemDriverVersionDelegate? _getDriverVersion;
    private UintValueDelegate? _nombreVentilateurs;
    private FanValueDelegate? _vitesseVentilateur, _cibleVentilateur, _politiqueVentilateur;
    private MinMaxDelegate? _plageVentilateur;
    private SetFanSpeedDelegate? _poserVentilateur;
    private FanDefaultDelegate? _rendreVentilateur;

    public bool IsAvailable
    {
        get { EnsureInit(); return _available; }
    }

    public bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes)
    {
        bar1TotalBytes = 0;
        fbTotalBytes = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _getMemory is null || _getBar1 is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;

            var mem = default(NvmlMemory);
            if (_getMemory(device, ref mem) != NvmlSuccess) return false;

            var bar1 = default(NvmlBar1Memory);
            if (_getBar1(device, ref bar1) != NvmlSuccess) return false;

            fbTotalBytes = mem.Total;
            bar1TotalBytes = bar1.Bar1Total;
            return fbTotalBytes != 0 && bar1TotalBytes != 0;
        }
        catch
        {
            bar1TotalBytes = 0;
            fbTotalBytes = 0;
            return false;
        }
    }

    public bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts)
    {
        milliwatts = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;

            // Lecture instantanée : marche sur les GPU de datacenter / plus anciens. Dépréciée et N/A sur les
            // cartes Ada grand public (RTX série 40, vérifié sur une 4060 / pilote 610.62 : rend NOT_SUPPORTED).
            if (_getPower is not null && _getPower(device, out milliwatts) == NvmlSuccess && milliwatts != 0)
                return true;

            // Repli : le tampon circulaire des relevés de puissance, moyenné sur la fenêtre.
            // Relevé du 25/09/2026, RTX 4060 / pilote 616.56 : nvidia-smi affiche N/A pour la consommation instantanée
            // et moyenne, alors que ce tampon rend 119 relevés frais (le plus ancien à 2,6 s) à 55,7 W, carte tenue en P0.
            return TryAveragePowerSamples(device, out milliwatts);
        }
        catch { milliwatts = 0; return false; }
    }

    private bool TryAveragePowerSamples(IntPtr device, out uint milliwatts)
    {
        milliwatts = 0;
        if (_getSamples is null) return false;
        var samples = new NvmlSample[120]; // le pilote garde ~119 relevés ; un tampon fixe évite la danse des 2 appels pour la taille
        uint count = (uint)samples.Length;
        if (_getSamples(device, NvmlTotalPowerSamples, 0UL, out _, ref count, samples) != NvmlSuccess || count == 0)
            return false;
        ulong sum = 0;
        uint n = Math.Min(count, (uint)samples.Length);
        for (uint i = 0; i < n; i++)
            sum += (uint)samples[i].Value; // les relevés de puissance sont des mW en entier non signé dans l'union de valeur
        milliwatts = (uint)(sum / n);
        return milliwatts != 0;
    }

    public bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius)
    {
        celsius = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _getTemp is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            return _getTemp(device, NvmlTemperatureGpu, out celsius) == NvmlSuccess;
        }
        catch { celsius = 0; return false; }
    }

    /// <summary>Le lien PCIe : generation et largeur negociees maintenant, et le maximum que la carte et
    /// le slot savent faire. Au repos une carte NVIDIA redescend souvent en generation 1 pour
    /// economiser : l'appelant doit lire sous charge avant de conclure a un lien bride.</summary>
    public bool TryGetPcieLink(int deviceIndex, out uint gen, out uint largeur, out uint genMax, out uint largeurMax)
    {
        gen = largeur = genMax = largeurMax = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _pcieGen is null || _pcieLargeur is null
            || _pcieGenMax is null || _pcieLargeurMax is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            return _pcieGen(device, out gen) == NvmlSuccess && _pcieLargeur(device, out largeur) == NvmlSuccess
                && _pcieGenMax(device, out genMax) == NvmlSuccess && _pcieLargeurMax(device, out largeurMax) == NvmlSuccess;
        }
        catch { gen = largeur = genMax = largeurMax = 0; return false; }
    }

    /// <summary>La memoire video : frequence actuelle et maximale. Au repos elle doit redescendre ; restee
    /// au maximum sans charge, elle consomme en permanence (cas connu des ecrans a frequences melangees).</summary>
    public bool TryGetMemoryClockMhz(int deviceIndex, out uint mhz, out uint maxMhz)
    {
        mhz = maxMhz = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _getClock is null || _getMaxClock is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            return _getClock(device, NvmlClockMem, out mhz) == NvmlSuccess && mhz != 0
                && _getMaxClock(device, NvmlClockMem, out maxMhz) == NvmlSuccess && maxMhz != 0;
        }
        catch { mhz = maxMhz = 0; return false; }
    }

    /// <summary>Les processus qui utilisent la carte pour l'affichage en ce moment (pid). Ce sont eux qui
    /// peuvent l'empecher de redescendre au repos. Faux si NVML ne sait pas les lister ici.</summary>
    public bool TryGetGraphicsProcesses(int deviceIndex, out uint[] pids)
    {
        pids = Array.Empty<uint>();
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _processusGraphiques is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            uint n = 0;
            var rc = _processusGraphiques(device, ref n, null);
            if (rc == NvmlSuccess) return true; // aucun processus
            if (rc != 7 || n == 0) return false;  // 7 = NVML_ERROR_INSUFFICIENT_SIZE : n porte la taille
            var infos = new NvmlProcessInfo[n + 8];
            n = (uint)infos.Length;
            if (_processusGraphiques(device, ref n, infos) != NvmlSuccess) return false;
            pids = infos.Take((int)n).Select(i => i.Pid).ToArray();
            return true;
        }
        catch { pids = Array.Empty<uint>(); return false; }
    }

    public bool TryGetClockMhz(int deviceIndex, out uint mhz)
    {
        mhz = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _getClock is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            // NVML_ERROR_NOT_SUPPORTED est un « pas lisible ici », pas un zero. Le contrat
            // fail-clean le traduit en false, et l'appelant en null.
            return _getClock(device, NvmlClockGraphics, out mhz) == NvmlSuccess && mhz != 0;
        }
        catch { mhz = 0; return false; }
    }

    public bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons)
    {
        reasons = 0;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null || _getClocksEventReasons is null) return false;
        try
        {
            if (_getHandle((uint)deviceIndex, out var device) != NvmlSuccess || device == IntPtr.Zero)
                return false;
            // Ici zero est une vraie valeur : nvmlClocksEventReasonNone = 0x0 signifie « rien
            // ne bride ». On ne peut donc pas utiliser « != 0 » comme preuve de lecture reussie,
            // contrairement aux watts ou aux MHz. Seul le code de retour fait foi.
            return _getClocksEventReasons(device, out reasons) == NvmlSuccess;
        }
        catch { reasons = 0; return false; }
    }

    public bool TryGetDriverVersion(out string version)
    {
        version = string.Empty;
        EnsureInit();
        if (!_available || _getDriverVersion is null) return false;
        try
        {
            var buffer = new byte[NvmlDriverVersionBufferSize];
            if (_getDriverVersion(buffer, (uint)buffer.Length) != NvmlSuccess) return false;

            // Buffer C : la chaine s'arrete au premier NUL. Prendre tout le buffer donnerait
            // "580.88\0\0\0..." : une chaine qui a l'air juste, qui compare faux, et dont
            // personne ne verrait le probleme avant qu'un test d'egalite echoue.
            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) end = buffer.Length;
            version = System.Text.Encoding.ASCII.GetString(buffer, 0, end).Trim();
            return version.Length > 0;
        }
        catch { version = string.Empty; return false; }
    }

    public bool TryLireVentilateurs(int deviceIndex, out IReadOnlyList<VentilateurNvml> ventilateurs)
    {
        ventilateurs = [];
        if (!Appareil(deviceIndex, out var device) || _nombreVentilateurs is null || _vitesseVentilateur is null
            || _politiqueVentilateur is null) return false;
        try
        {
            if (_nombreVentilateurs(device, out var nombre) != NvmlSuccess || nombre == 0) return false;
            uint? min = null, max = null;
            if (_plageVentilateur is not null && _plageVentilateur(device, out var mn, out var mx) == NvmlSuccess) { min = mn; max = mx; }
            var lus = new List<VentilateurNvml>();
            for (uint i = 0; i < nombre; i++)
            {
                if (_vitesseVentilateur(device, i, out var vitesse) != NvmlSuccess
                    || _politiqueVentilateur(device, i, out var politique) != NvmlSuccess) return false;
                uint? cible = null;
                if (_cibleVentilateur is not null && _cibleVentilateur(device, i, out var c) == NvmlSuccess) cible = c;
                lus.Add(new VentilateurNvml((int)i, vitesse, cible, politique, min, max));
            }
            ventilateurs = lus;
            return true;
        }
        catch { ventilateurs = []; return false; }
    }

    public bool TryPoserVentilateur(int deviceIndex, int ventilateur, uint pourcent)
    {
        if (ventilateur < 0 || pourcent > 100 || !Appareil(deviceIndex, out var device) || _poserVentilateur is null) return false;
        try { return _poserVentilateur(device, (uint)ventilateur, pourcent) == NvmlSuccess; }
        catch { return false; }
    }

    public bool TryRendreVentilateur(int deviceIndex, int ventilateur)
    {
        if (ventilateur < 0 || !Appareil(deviceIndex, out var device) || _rendreVentilateur is null) return false;
        try { return _rendreVentilateur(device, (uint)ventilateur) == NvmlSuccess; }
        catch { return false; }
    }

    private bool Appareil(int deviceIndex, out IntPtr device)
    {
        device = IntPtr.Zero;
        if (deviceIndex < 0) return false;
        EnsureInit();
        if (!_available || _getHandle is null) return false;
        try { return _getHandle((uint)deviceIndex, out device) == NvmlSuccess && device != IntPtr.Zero; }
        catch { device = IntPtr.Zero; return false; }
    }

    private void EnsureInit() => _ = _init.Value;

    private bool Initialiser()
    {
            try
            {
                if (!TryLoadNvml(out var lib)) return false;

                var init = Resolve<InitDelegate>(lib, "nvmlInit_v2");
                _getHandle = Resolve<HandleByIndexDelegate>(lib, "nvmlDeviceGetHandleByIndex_v2");
                _getMemory = Resolve<MemoryInfoDelegate>(lib, "nvmlDeviceGetMemoryInfo");
                _getBar1 = Resolve<Bar1MemoryInfoDelegate>(lib, "nvmlDeviceGetBAR1MemoryInfo");
                // Puissance / température sont une capacité en plus (l'instrument d'efficacité de l'undervolt) ;
                // un pilote trop ancien pour les exporter ne doit pas désactiver la détection du ReBAR : elles
                // sont résolues au mieux et chaque lecteur vérifie que son délégué n'est pas null.
                _getPower = Resolve<PowerUsageDelegate>(lib, "nvmlDeviceGetPowerUsage");
                _getTemp = Resolve<TemperatureDelegate>(lib, "nvmlDeviceGetTemperature");
                _pcieGen = Resolve<UintValueDelegate>(lib, "nvmlDeviceGetCurrPcieLinkGeneration");
                _getMaxClock = Resolve<ClockInfoDelegate>(lib, "nvmlDeviceGetMaxClockInfo");
                _processusGraphiques = Resolve<ProcessesDelegate>(lib, "nvmlDeviceGetGraphicsRunningProcesses_v3")
                    ?? Resolve<ProcessesDelegate>(lib, "nvmlDeviceGetGraphicsRunningProcesses_v2");
                _pcieLargeur = Resolve<UintValueDelegate>(lib, "nvmlDeviceGetCurrPcieLinkWidth");
                _pcieGenMax = Resolve<UintValueDelegate>(lib, "nvmlDeviceGetMaxPcieLinkGeneration");
                _pcieLargeurMax = Resolve<UintValueDelegate>(lib, "nvmlDeviceGetMaxPcieLinkWidth");
                _getSamples = Resolve<SamplesDelegate>(lib, "nvmlDeviceGetSamples");
                _getDriverVersion = Resolve<SystemDriverVersionDelegate>(lib, "nvmlSystemGetDriverVersion");
                // Le ventilateur, exports documentés de NVML ; chacun garde son délégué.
                _nombreVentilateurs = Resolve<UintValueDelegate>(lib, "nvmlDeviceGetNumFans");
                _vitesseVentilateur = Resolve<FanValueDelegate>(lib, "nvmlDeviceGetFanSpeed_v2");
                _cibleVentilateur = Resolve<FanValueDelegate>(lib, "nvmlDeviceGetTargetFanSpeed");
                _politiqueVentilateur = Resolve<FanValueDelegate>(lib, "nvmlDeviceGetFanControlPolicy_v2");
                _plageVentilateur = Resolve<MinMaxDelegate>(lib, "nvmlDeviceGetMinMaxFanSpeed");
                _poserVentilateur = Resolve<SetFanSpeedDelegate>(lib, "nvmlDeviceSetFanSpeed_v2");
                _rendreVentilateur = Resolve<FanDefaultDelegate>(lib, "nvmlDeviceSetDefaultFanSpeed_v2");

                // Frequence et raison de bridage : API publiques et documentees de NVML, sans NVAPI ni
                // identifiants prives (verifie dans la doc officielle le 08/09) :
                // nvmlDeviceGetClockInfo et nvmlDeviceGetCurrentClocksEventReasons sont des
                // exports nommes et stables, au meme titre que la puissance et la temperature.
                // Source : docs.nvidia.com/deploy/nvml-api, groupes nvmlDeviceQueries et
                // nvmlClocksEventReasons.
                _getClock = Resolve<ClockInfoDelegate>(lib, "nvmlDeviceGetClockInfo");
                // Le nom actuel d'abord ; l'ancien ensuite, pour les pilotes anterieurs au
                // renommage ThrottleReasons vers EventReasons. Meme signature, meme bitmask.
                _getClocksEventReasons =
                    Resolve<ClocksEventReasonsDelegate>(lib, "nvmlDeviceGetCurrentClocksEventReasons")
                    ?? Resolve<ClocksEventReasonsDelegate>(lib, "nvmlDeviceGetCurrentClocksThrottleReasons");

                if (init is null || _getHandle is null || _getMemory is null || _getBar1 is null)
                    return false;

                return init() == NvmlSuccess;
            }
            catch
            {
                return false;
            }
    }

    private static bool TryLoadNvml(out IntPtr lib)
    {
        // System32 seulement : la surcharge sans chemin de recherche suit l'ordre par defaut de
        // Windows, PATH compris (voir DefaultDllImportSearchPaths dans AdlxNative.cs).
        if (NativeLibrary.TryLoad("nvml.dll", typeof(NvmlNative).Assembly,
                DllImportSearchPath.System32, out lib)) return true;
        if (NativeLibrary.TryLoad(NvsmiFallback, out lib)) return true;
        lib = IntPtr.Zero;
        return false;
    }

    private static T? Resolve<T>(IntPtr lib, string name) where T : Delegate
    {
        try
        {
            return NativeLibrary.TryGetExport(lib, name, out var p) && p != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<T>(p)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // --- délégués (Cdecl par symétrie ; sans effet sur x64) ---

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HandleByIndexDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MemoryInfoDelegate(IntPtr device, ref NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Bar1MemoryInfoDelegate(IntPtr device, ref NvmlBar1Memory bar1);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerUsageDelegate(IntPtr device, out uint milliwatts);

    // nvmlSystemGetDriverVersion(char* version, unsigned int length) : remplit un buffer ASCII
    // avec la branche NVIDIA telle que l'utilisateur la connait ("580.88"), pas la quadruplette
    // WDDM de WMI. C'est une fonction systeme : aucun handle de device, aucune elevation.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SystemDriverVersionDelegate(byte[] version, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TemperatureDelegate(IntPtr device, int sensorType, out uint celsius);
    private delegate int UintValueDelegate(IntPtr device, out uint value);

    /// <summary>nvmlDeviceGetClockInfo(device, nvmlClockType_t, unsigned int*).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClockInfoDelegate(IntPtr device, int clockType, out uint mhz);
    private delegate int ProcessesDelegate(IntPtr device, ref uint count, [Out] NvmlProcessInfo[]? infos);

    /// <summary>nvmlDeviceGetCurrentClocksEventReasons(device, unsigned long long*).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClocksEventReasonsDelegate(IntPtr device, out ulong reasons);

    // Ventilateur (nvml.h) : (device, fan, unsigned int*) ; plage (device, unsigned int* min, unsigned int* max) ;
    // pose (device, fan, unsigned int speed) ; retour à l'automatique (device, fan).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FanValueDelegate(IntPtr device, uint fan, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MinMaxDelegate(IntPtr device, out uint min, out uint max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetFanSpeedDelegate(IntPtr device, uint fan, uint speed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FanDefaultDelegate(IntPtr device, uint fan);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SamplesDelegate(
        IntPtr device, int samplingType, ulong lastSeenTimeStamp,
        out int sampleValType, ref uint sampleCount, [In, Out] NvmlSample[] samples);

    // --- structs (ordre et types de champs exacts de nvml.h) ---

    // nvmlProcessInfo_t (v2/v3) : pid, memoire utilisee, instances GPU/calcul.
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessInfo { public uint Pid; public ulong UsedGpuMemory; public uint GpuInstanceId; public uint ComputeInstanceId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlBar1Memory
    {
        public ulong Bar1Total;
        public ulong Bar1Free;
        public ulong Bar1Used;
    }

    // nvmlSample_t { ull timeStamp; nvmlValue_t sampleValue; } : l'union de valeur fait 8 octets ;
    // pour les relevés de puissance, elle tient des mW en entier non signé dans ses 32 bits bas (x64 petit-boutiste).
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlSample
    {
        public ulong TimeStamp;
        public ulong Value;
    }
}
