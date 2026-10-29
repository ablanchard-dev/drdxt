namespace DrDxt.GpuNative.Nvml;

/// <summary>Frontière managée mince sur nvml.dll (NVIDIA Management Library, la base de nvidia-smi). Contrairement à
/// NVAPI (QueryInterface seulement, ids privés rétro-ingéniés), NVML expose des exports nommés, stables et documentés
/// (docs.nvidia.com/deploy/nvml-api) : un P/Invoke direct. Elle lit BAR1 et le framebuffer (d'où l'état du Resizable
/// BAR : activé ⇒ BAR1 ≈ toute la VRAM, désactivé ⇒ ~256 Mio ; nvmlDeviceGetBAR1MemoryInfo est l'appel de nvidia-smi
/// pour « BAR1 Memory Usage »), la consommation, la température, la fréquence, les raisons de bridage, la version du
/// pilote, le lien PCIe, la mémoire vidéo et les ventilateurs, sans élévation, et porte deux écritures de ventilateur
/// (false par défaut). Ne lève jamais : false sur tout échec (DLL absente, pas de GPU NVIDIA, init ratée, mauvais index, panne du
/// pilote), paramètres out à leur valeur par défaut.</summary>
public interface INvmlNative
{
    /// <summary>Vrai quand nvml.dll est chargée et que nvmlInit_v2 a réussi (GPU NVIDIA + pilote présents).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Lit le total de BAR1 et le total du framebuffer (VRAM), en octets, pour le périphérique NVML
    /// d'index <paramref name="deviceIndex"/> (nvmlDeviceGetBAR1MemoryInfo + nvmlDeviceGetMemoryInfo).
    /// Rend false sur un mauvais index, sans GPU NVIDIA, ou sur une panne du pilote. Les deux totaux
    /// sont non nuls quand le retour est true.
    /// </summary>
    bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes);

    /// <summary>
    /// Consommation de la carte en milliwatts pour le périphérique d'index <paramref name="deviceIndex"/>.
    /// La lecture instantanée (nvmlDeviceGetPowerUsage), sinon la moyenne du tampon de relevés (nvmlDeviceGetSamples) :
    /// l'instantanée est dépréciée / N/A sur les Ada grand public (RTX série 40). L'instrument d'efficacité de
    /// l'undervolt (à performance égale, elle baisse). False sans lecture non nulle.
    /// </summary>
    bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts);

    /// <summary>
    /// Température actuelle du cœur du GPU en Celsius (nvmlDeviceGetTemperature, NVML_TEMPERATURE_GPU).
    /// Confirme le bénéfice de l'undervolt (tension plus basse ⇒ température plus basse ⇒ marge de boost).
    /// </summary>
    bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius);

    /// <summary>
    /// Frequence graphique courante en MHz (nvmlDeviceGetClockInfo, NVML_CLOCK_GRAPHICS ; groupe nvmlDeviceQueries).
    /// false quand le peripherique ne la rend pas (NVML_ERROR_NOT_SUPPORTED) : jamais un zero fabrique.
    /// </summary>
    bool TryGetClockMhz(int deviceIndex, out uint mhz);

    /// <summary>
    /// Bitmask des raisons pour lesquelles l'horloge est actuellement bridee
    /// (nvmlDeviceGetCurrentClocksEventReasons, avec repli sur l'ancien nom
    /// nvmlDeviceGetCurrentClocksThrottleReasons ; groupe nvmlClocksEventReasons). <c>0</c> est une vraie valeur
    /// (nvmlClocksEventReasonNone, « rien ne bride ») : seul le booleen distingue « rien ne bride » de « pas lisible ».
    /// </summary>
    bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons);

    /// <summary>
    /// Branche du pilote NVIDIA telle que l'utilisateur la connait ("580.88"), via nvmlSystemGetDriverVersion : la
    /// seule source sans elevation (Win32_VideoController.DriverVersion rend la quadruplette WDDM "32.0.16.1088").
    /// false quand NVML est absent ou que l'appel echoue, jamais une version fabriquee.
    /// </summary>
    bool TryGetDriverVersion(out string version);
    /// <summary>Lien PCIe negocie et maximum (NVML). Faux si non lu.</summary>
    bool TryGetPcieLink(int deviceIndex, out uint gen, out uint largeur, out uint genMax, out uint largeurMax) { gen = largeur = genMax = largeurMax = 0; return false; }
    /// <summary>Frequence de la memoire video, actuelle et maximale (NVML). Faux si non lue.</summary>
    bool TryGetMemoryClockMhz(int deviceIndex, out uint mhz, out uint maxMhz) { mhz = maxMhz = 0; return false; }

    /// <summary>Chaque ventilateur de la carte, lu par NVML (docs.nvidia.com/deploy/nvml-api,
    /// nvmlDeviceGetNumFans, GetFanSpeed_v2, GetTargetFanSpeed, GetFanControlPolicy_v2, GetMinMaxFanSpeed). Faux si non lu.
    /// Lu sur la RTX 4060 de référence : 1 ventilateur, politique 0 (automatique), 0 % au repos, plage 30 à 100.</summary>
    bool TryLireVentilateurs(int deviceIndex, out IReadOnlyList<VentilateurNvml> ventilateurs) { ventilateurs = []; return false; }

    /// <summary>Pose une vitesse fixe (nvmlDeviceSetFanSpeed_v2) : la politique passe en manuel. NVIDIA prévient qu'une
    /// vitesse trop basse peut abîmer la carte ; l'appelant (le helper) ne pose jamais sous la vitesse automatique.</summary>
    bool TryPoserVentilateur(int deviceIndex, int ventilateur, uint pourcent) => false;

    /// <summary>Rend le ventilateur à l'automatique (nvmlDeviceSetDefaultFanSpeed_v2). Le retour se prouve en relisant la
    /// politique : un retour à l'automatique raté a été signalé en 2022 (forum NVIDIA, sous Linux).</summary>
    bool TryRendreVentilateur(int deviceIndex, int ventilateur) => false;
}

/// <summary>Un ventilateur lu par NVML. <see cref="Politique"/> 0 = automatique (NVML_FAN_POLICY_TEMPERATURE_CONTINOUS_SW),
/// 1 = manuel. Cible et plage valent null quand le pilote ne les rend pas : elles servent de plancher à l'écriture.</summary>
public sealed record VentilateurNvml(int Index, uint VitessePct, uint? CiblePct, uint Politique, uint? MinPct, uint? MaxPct)
{
    public const uint PolitiqueAutomatique = 0;
    public bool Automatique => Politique == PolitiqueAutomatique;
}
