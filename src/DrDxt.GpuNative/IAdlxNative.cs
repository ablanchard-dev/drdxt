namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// IAdlxNative — the thin managed boundary over the ADLX native surface.
//
// This is the only gateway to amdadlx64.dll from managed code. All vtable
// navigation, DllImport, and native lifetime live in AdlxNative.
// Unit tests replace this with FakeAdlxNative — no native calls in tests.
//
// Fail-clean contract:
//   • IsAvailable == false  ⇒ DLL absent or init failed; every Try* returns false.
//   • Every Try* method never throws; returns false on any fault.
//   • out params are default-initialised on false return.
// -----------------------------------------------------------------------

/// <summary>Mirrors ADLX_IntRange { minValue, maxValue, step }.</summary>
public readonly struct AdlxIntRange
{
    public int Min  { get; init; }
    public int Max  { get; init; }
    public int Step { get; init; }
}

/// <summary>Graphics-tuning reads from IADLXManualGraphicsTuning2:
/// voltage offset (mV), max-frequency offset (MHz), min-frequency (MHz)
/// and their supported ranges.</summary>
public readonly struct AdlxGfxReads
{
    public int          VoltMv       { get; init; }
    public AdlxIntRange VoltRange    { get; init; }
    public int          MaxFreqMhz   { get; init; }
    public AdlxIntRange MaxFreqRange { get; init; }
    public int          MinFreqMhz   { get; init; }
    public AdlxIntRange MinFreqRange { get; init; }
}

/// <summary>Power-limit reads from IADLXManualPowerTuning:
/// current power-limit (%) and its supported range.</summary>
public readonly struct AdlxPowerReads
{
    public int          PowerLimitPct { get; init; }
    public AdlxIntRange PowerRange    { get; init; }
}

/// <summary>One point on a GPU fan curve.</summary>
public readonly struct AdlxFanPoint
{
    public int TempC    { get; init; }
    public int SpeedPct { get; init; }
}

/// <summary>Fan-tuning reads from IADLXManualFanTuning:
/// current fan-curve states and the supported temperature/speed ranges.</summary>
public readonly struct AdlxFanReads
{
    public IReadOnlyList<AdlxFanPoint>? Points     { get; init; }
    public AdlxIntRange                 TempRange  { get; init; }
    public AdlxIntRange                 SpeedRange { get; init; }
}

/// <summary>One step of the ADLX GFX read-path diagnostic walk (see
/// <see cref="IAdlxNative.ProbeGfxPath"/>). <paramref name="Name"/> is the stage,
/// <paramref name="Ok"/> whether it succeeded, <paramref name="Detail"/> a short
/// human reason. Used so a skipped GPU gate on real AMD hardware reports exactly
/// where ADLX stopped instead of a vague "not available".</summary>
public sealed record GpuProbeStep(string Name, bool Ok, string? Detail = null);

/// <summary>A1 (06/10/2026) : les réglages AMD lus par ADLX sur la carte dédiée. Chaque champ à null = non lu
/// (DLL absente, carte sans la fonction, entrée qui rend une erreur ADLX) ; jamais une valeur par défaut.
/// <paramref name="Raison"/> dit pourquoi rien n'a été lu quand c'est le cas.</summary>
public sealed record AdlxReglagesLus(
    string? Raison = null,
    bool? Sam = null,
    bool? AntiLag = null,
    bool? FrtcActif = null,
    int? FrtcFps = null,
    bool? ChillActif = null,
    int? ChillMinFps = null,
    int? ChillMaxFps = null,
    double? TemperatureC = null,
    // A2b (07/10/2026) : la cible FRTC réglée, lue même quand FRTC est éteint (la rendre exacte après un essai).
    int? FrtcFpsReglee = null,
    // A2c (08/10/2026) : le mode Vsync du pilote (0 à 3) et Enhanced Sync, qu'allumer la Vsync éteint.
    int? VsyncMode = null,
    bool? EnhancedSyncActif = null)
{
    public static AdlxReglagesLus RienLu(string raison) => new(Raison: raison);

    /// <summary>A2b : l'état FRTC en un entier, celui de <c>AdlxTuningKind.FrameRateTarget</c> : allumé = sa cible
    /// (positive), éteint = moins la cible réglée, null si l'un des deux n'est pas lu.</summary>
    public int? FrtcCode => FrtcActif switch
    {
        true => FrtcFps is > 0 and var f ? f : null,
        false => FrtcFpsReglee is > 0 and var r ? -r : null,
        null => null,
    };

    /// <summary>La limite d'images que le pilote impose, quand elle est lue : la plus basse des limites
    /// actives (Chill plafonne à son maximum, FRTC à sa cible), 0 quand les deux sont lues éteintes,
    /// null sinon. Les deux s'excluent dans le logiciel AMD ; on ne le suppose pas.</summary>
    public int? LimiteImages
    {
        get
        {
            // Les deux états doivent être lus (une fonction absente de la carte est lue éteinte), et une limite
            // allumée doit avoir sa valeur : sinon on ne sait pas laquelle s'applique.
            if (FrtcActif is null || ChillActif is null) return null;
            if ((FrtcActif == true && FrtcFps is not > 0) || (ChillActif == true && ChillMaxFps is not > 0)) return null;
            var actives = new[] { FrtcActif == true ? FrtcFps : null, ChillActif == true ? ChillMaxFps : null }.OfType<int>().ToList();
            return actives.Count > 0 ? actives.Min() : 0;
        }
    }
}

/// <summary>A-FS1 (08/10/2026) : un écran vu par le pilote AMD, son nom (celui de l'EDID) et l'état de FreeSync.
/// null = non lu.</summary>
public sealed record AdlxEcranFreeSync(string Nom, bool? Supporte, bool? Actif);

/// <summary>Thin boundary over native ADLX. Never throws — returns false on any
/// failure (absent DLL, bad vtable, unsupported interface, etc.).</summary>
public interface IAdlxNative
{
    /// <summary>True when ADLXInitialize succeeded (AMD GPU + Adrenalin driver present).</summary>
    bool IsAvailable { get; }

    /// <summary>Reads GPU graphics tuning via IADLXManualGraphicsTuning2 (RDNA) or
    /// IADLXManualGraphicsTuning1 (Vega/older) for the given GPU index.
    /// Returns false if the index is out of range or the interface is not supported.</summary>
    bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads);

    /// <summary>Reads power-limit tuning via IADLXManualPowerTuning.
    /// Returns false if the index is out of range or not supported.</summary>
    bool TryReadPower(int gpuIndex, out AdlxPowerReads reads);

    /// <summary>Reads fan-curve tuning via IADLXManualFanTuning.
    /// Returns false if the index is out of range or not supported.</summary>
    bool TryReadFan(int gpuIndex, out AdlxFanReads reads);

    // ── write methods (harness-only, never called during dotnet test) ─────────

    /// <summary>
    /// Get-then-set: reads the current GFX state first (capturing it in
    /// <paramref name="original"/> for revert), validates the proposed values against
    /// the hardware's own GetGPUMaxFrequencyRange / GetGPUVoltageRange, then writes:
    ///   SetGPUMaxFrequency  IADLXManualGraphicsTuning2 vtable slot 8  (verified vs ADLX SDK headers)
    ///   SetGPUVoltage       IADLXManualGraphicsTuning2 vtable slot 11 (verified vs ADLX SDK headers)
    /// If SetGPUVoltage fails after SetGPUMaxFrequency already succeeded, best-effort
    /// rolls back MaxFreq to the original before returning false.
    /// Returns false if unavailable, unsupported (non-RDNA), range-violation, or any fault.
    /// Never throws.
    /// </summary>
    bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original);

    /// <summary>
    /// Get-then-set: reads the current power limit first (capturing it in
    /// <paramref name="original"/>), validates the proposed value against
    /// the hardware's GetPowerLimitRange, then writes:
    ///   SetPowerLimit  IADLXManualPowerTuning vtable slot 5 (verified vs ADLX SDK headers)
    /// Returns false if unavailable, unsupported, range-violation, or any fault.
    /// Never throws.
    /// </summary>
    bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original);

    // ── DIAGNOSTICS (read-only; never writes, never throws) ──────────────────

    /// <summary>Walks the GFX read path step by step — DLL/init → GetGPUs → list
    /// size/index → GetGPU → GetGPUTuningServices → IsSupportedManualGFXTuning →
    /// GetManualGFXTuning → QueryInterface(Tuning2) → ReadGfxV2 — recording each
    /// stage's outcome. Stops at the first failure with an honest reason. Lets a
    /// skipped GPU gate report exactly where ADLX stopped on a given machine.
    /// Read-only; performs no write; never throws.</summary>
    IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex);

    /// <summary>A1 : SAM, Anti-Lag, FRTC, Chill et température de la carte dédiée. Lecture seule ; ne lève
    /// jamais. Par défaut (doublures qui ne la portent pas) : rien lu.</summary>
    AdlxReglagesLus LireReglages() => AdlxReglagesLus.RienLu("Cette couture ne lit pas les réglages ADLX.");

    /// <summary>A2 (07/10/2026) : allume ou éteint Anti-Lag (le classique ; le niveau Anti-Lag Next n'est jamais
    /// touché) sur la carte dédiée, celle que <see cref="LireReglages"/> lit. Rend <c>null</c> si écrit, sinon la
    /// raison ; ne lève jamais. Par défaut (doublures qui ne la portent pas) : refusé.</summary>
    string? EcrireAntiLag(bool actif) => "Cette couture n'écrit pas Anti-Lag.";

    /// <summary>A2b (07/10/2026) : pose FRTC sur la carte dédiée. <paramref name="code"/> &gt; 0 = allumé à cette cible ;
    /// &lt; 0 = éteint, cible rendue à <c>-code</c> ; 0 refusé. La cible doit tenir dans GetFPSRange. Rend <c>null</c>
    /// si écrit, sinon la raison ; ne lève jamais.</summary>
    string? EcrireFrtc(int code) => "Cette couture n'écrit pas FRTC.";

    /// <summary>A2c (08/10/2026) : pose le mode Vsync du pilote (0 à 3, ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE) sur la
    /// carte dédiée. Rend <c>null</c> si écrit, sinon la raison ; ne lève jamais.</summary>
    string? EcrireVsync(int mode) => "Cette couture n'écrit pas la Vsync.";

    /// <summary>A-FS1 : FreeSync de chaque écran branché sur la carte AMD, lu au pilote. Vide = rien lu.</summary>
    IReadOnlyList<AdlxEcranFreeSync> LireFreeSync() => Array.Empty<AdlxEcranFreeSync>();
}
