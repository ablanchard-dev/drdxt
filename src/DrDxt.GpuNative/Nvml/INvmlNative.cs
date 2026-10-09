namespace DrDxt.GpuNative.Nvml;

// -----------------------------------------------------------------------
// INvmlNative — the thin managed boundary over nvml.dll (NVIDIA Management Library).
//
// NVML is the same library nvidia-smi is built on. Unlike NVAPI (QueryInterface-only,
// reverse-engineered private ids), NVML exposes stable, documented, named exports, so
// this boundary is a straight P/Invoke — no function-id table, no recipe.
//
// We use it for ONE thing: reading BAR1 size + framebuffer size, from which Resizable BAR
// (ReBAR) state is inferred (ReBAR on ⇒ BAR1 ≈ full VRAM; off ⇒ ~256 MiB legacy window).
// Source: nvmlDeviceGetBAR1MemoryInfo (docs.nvidia.com/deploy/nvml-api), the exact call
// nvidia-smi makes for its "BAR1 Memory Usage" line.
//
// Fail-clean contract (mirrors INvApiNative / IAdlxNative):
//   • IsAvailable == false ⇒ nvml.dll absent, no NVIDIA GPU, or nvmlInit failed.
//   • Every Try* never throws; returns false on any fault; out params default-initialised.
// -----------------------------------------------------------------------

/// <summary>Thin boundary over native NVML. Never throws — returns false on any failure
/// (absent DLL, init failure, bad index, driver fault).</summary>
public interface INvmlNative
{
    /// <summary>True when nvml.dll loaded and nvmlInit_v2 succeeded (an NVIDIA GPU + driver present).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reads the BAR1 total and framebuffer (VRAM) total, in bytes, for the NVML device at
    /// <paramref name="deviceIndex"/> (nvmlDeviceGetBAR1MemoryInfo + nvmlDeviceGetMemoryInfo).
    /// Returns false on a bad index, no NVIDIA GPU, or a driver fault. Both totals are non-zero
    /// on a true return.
    /// </summary>
    bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes);

    /// <summary>
    /// Board power draw in milliwatts for the device at <paramref name="deviceIndex"/>. Tries the
    /// instant read (nvmlDeviceGetPowerUsage) first, then falls back to averaging the power-samples
    /// ring buffer (nvmlDeviceGetSamples) — the instant read is DEPRECATED / N/A on Ada consumer
    /// cards (RTX 40-series), where the sample buffer is the only path to real watts. Read-only,
    /// non-elevated. This is the efficiency instrument for the undervolt lever: at iso-performance,
    /// a real undervolt lowers this. Returns false if neither path yields a non-zero reading.
    /// </summary>
    bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts);

    /// <summary>
    /// Current GPU core temperature in Celsius (nvmlDeviceGetTemperature, NVML_TEMPERATURE_GPU).
    /// Read-only, non-elevated. Corroborates the undervolt benefit (lower voltage ⇒ lower temp ⇒
    /// more boost headroom). Returns false on a bad index, no NVIDIA GPU, or a driver fault.
    /// </summary>
    bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius);

    /// <summary>
    /// Branche du pilote NVIDIA telle que l'utilisateur la connait — "580.88" — via
    /// nvmlSystemGetDriverVersion. C'est la seule source d'un vrai numero de branche cote
    /// user-mode sans elevation : Win32_VideoController.DriverVersion rend la quadruplette
    /// WDDM ("32.0.16.1088"), dont le premier groupe est la version WDDM/DirectX et ne porte
    /// Aucun signal de fraicheur. false quand NVML est absent ou que l'appel echoue — jamais
    /// une version fabriquee.
    /// </summary>
    /// <summary>
    /// Frequence GRAPHIQUE courante en MHz (nvmlDeviceGetClockInfo, NVML_CLOCK_GRAPHICS).
    ///
    /// Le produit affirmait le 07/09/2026 que la frequence coeur « demanderait NVAPI et ses
    /// identifiants prives ». Verifie dans la doc officielle le 08/09 : c'est un export NVML
    /// nomme, documente et stable, exactement comme la puissance et la temperature. La gate
    /// etait fausse, et elle privait le root-cause de sa grandeur la plus directe.
    /// Source : docs.nvidia.com/deploy/nvml-api, groupe nvmlDeviceQueries.
    ///
    /// false quand le peripherique ne sait pas rendre cette horloge
    /// (NVML_ERROR_NOT_SUPPORTED) — jamais un zero fabrique.
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

    bool TryGetDriverVersion(out string version);
    /// <summary>Lien PCIe negocie et maximum (NVML). Faux si non lu.</summary>
    bool TryGetPcieLink(int deviceIndex, out uint gen, out uint largeur, out uint genMax, out uint largeurMax) { gen = largeur = genMax = largeurMax = 0; return false; }
    /// <summary>Frequence de la memoire video, actuelle et maximale (NVML). Faux si non lue.</summary>
    bool TryGetMemoryClockMhz(int deviceIndex, out uint mhz, out uint maxMhz) { mhz = maxMhz = 0; return false; }

    /// <summary>GPU-OC (08/10/2026) : chaque ventilateur de la carte, lu par NVML (docs.nvidia.com/deploy/nvml-api,
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
