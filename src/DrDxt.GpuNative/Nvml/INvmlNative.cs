namespace DrDxt.GpuNative.Nvml;

// -----------------------------------------------------------------------
// INvmlNative : la frontière managée mince sur nvml.dll (NVIDIA Management Library).
//
// NVML est la bibliothèque sur laquelle est construit nvidia-smi. Contrairement à NVAPI
// (QueryInterface seulement, ids privés rétro-ingéniés), NVML expose des exports nommés, stables et
// documentés : cette frontière est un P/Invoke direct, sans table d'ids de fonction ni recette.
//
// Ce qu'elle lit (source : docs.nvidia.com/deploy/nvml-api) :
//   • la taille de BAR1 + la taille du framebuffer, d'où l'on déduit l'état du Resizable BAR
//     (ReBAR activé ⇒ BAR1 ≈ toute la VRAM ; désactivé ⇒ fenêtre historique de ~256 Mio) ;
//     nvmlDeviceGetBAR1MemoryInfo est l'appel exact de nvidia-smi pour « BAR1 Memory Usage » ;
//   • la consommation, la température, la fréquence graphique et les raisons de bridage ;
//   • la version du pilote (branche NVIDIA, par exemple « 580.88 ») ;
//   • le lien PCIe (négocié et maximum) et la fréquence de la mémoire vidéo ;
//   • les ventilateurs (vitesse, cible, politique, plage).
// Elle porte aussi deux écritures de ventilateur, TryPoserVentilateur et TryRendreVentilateur
// (false par défaut pour une implémentation qui ne les porte pas).
//
// Contrat d'échec propre (comme INvApiNative / IAdlxNative) :
//   • IsAvailable == false ⇒ nvml.dll absente, pas de GPU NVIDIA, ou nvmlInit raté.
//   • Aucun Try* ne lève ; il rend false sur toute panne ; les paramètres out sont à leur valeur par défaut.
// -----------------------------------------------------------------------

/// <summary>Frontière mince sur NVML natif. Ne lève jamais : rend false sur tout échec (DLL
/// absente, init ratée, mauvais index, panne du pilote).</summary>
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
    /// Tente d'abord la lecture instantanée (nvmlDeviceGetPowerUsage), puis se rabat sur la moyenne du
    /// tampon circulaire de relevés de puissance (nvmlDeviceGetSamples) : la lecture instantanée est
    /// dépréciée / N/A sur les cartes Ada grand public (RTX série 40), où le tampon de relevés est le
    /// seul chemin vers de vrais watts. Lecture seule, non élevée. C'est l'instrument d'efficacité du
    /// levier d'undervolt : à performance égale, un vrai undervolt la fait baisser. Rend false si
    /// aucun des deux chemins ne donne une lecture non nulle.
    /// </summary>
    bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts);

    /// <summary>
    /// Température actuelle du cœur du GPU en Celsius (nvmlDeviceGetTemperature, NVML_TEMPERATURE_GPU).
    /// Lecture seule, non élevée. Confirme le bénéfice de l'undervolt (tension plus basse ⇒
    /// température plus basse ⇒ plus de marge de boost). Rend false sur un mauvais index, sans GPU
    /// NVIDIA, ou sur une panne du pilote.
    /// </summary>
    bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius);

    /// <summary>
    /// Frequence graphique courante en MHz (nvmlDeviceGetClockInfo, NVML_CLOCK_GRAPHICS).
    ///
    /// Pas besoin de NVAPI : c'est un export NVML nomme, documente et stable, exactement comme la
    /// puissance et la temperature. Source : docs.nvidia.com/deploy/nvml-api, groupe nvmlDeviceQueries.
    ///
    /// false quand le peripherique ne sait pas rendre cette horloge
    /// (NVML_ERROR_NOT_SUPPORTED) : jamais un zero fabrique.
    /// </summary>
    bool TryGetClockMhz(int deviceIndex, out uint mhz);

    /// <summary>
    /// Bitmask des raisons pour lesquelles l'horloge est actuellement bridee
    /// (nvmlDeviceGetCurrentClocksEventReasons, avec repli sur l'ancien nom
    /// nvmlDeviceGetCurrentClocksThrottleReasons).
    ///
    /// Ici <c>0</c> est une vraie valeur : nvmlClocksEventReasonNone vaut 0x0 et signifie
    /// « rien ne bride ». Le booleen de retour est donc la seule facon de distinguer « rien ne
    /// bride » de « pas lisible ».
    ///
    /// Source : docs.nvidia.com/deploy/nvml-api, groupe nvmlClocksEventReasons.
    /// </summary>
    bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons);

    /// <summary>
    /// Branche du pilote NVIDIA telle que l'utilisateur la connait ("580.88"), via
    /// nvmlSystemGetDriverVersion. C'est la seule source d'un vrai numero de branche cote
    /// user-mode sans elevation : Win32_VideoController.DriverVersion rend la quadruplette
    /// WDDM ("32.0.16.1088"), dont le premier groupe est la version WDDM/DirectX. false quand
    /// NVML est absent ou que l'appel echoue, jamais une version fabriquee.
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
/// 1 = manuel.</summary>
public sealed record VentilateurNvml(int Index, uint VitessePct, uint CiblePct, uint Politique, uint MinPct, uint MaxPct)
{
    public const uint PolitiqueAutomatique = 0;
    public bool Automatique => Politique == PolitiqueAutomatique;
}
