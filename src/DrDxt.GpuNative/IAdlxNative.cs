namespace DrDxt.Detection.Gpu.Adlx;

// IAdlxNative : le seul accès managé à amdadlx64.dll (vtables, DllImport et durée de vie native dans AdlxNative ;
// FakeAdlxNative en test, aucun appel natif). IsAvailable == false ⇒ DLL absente ou init ratée, chaque Try* rend false ;
// aucun Try* ne lève, false sur toute panne, paramètres out à leur valeur par défaut.

/// <summary>Reflet d'ADLX_IntRange { minValue, maxValue, step }.</summary>
public readonly struct AdlxIntRange
{
    public int Min  { get; init; }
    public int Max  { get; init; }
    public int Step { get; init; }
}

/// <summary>Lectures de réglage graphique depuis IADLXManualGraphicsTuning2 : tension (mV), fréquence max (MHz),
/// fréquence min (MHz), des valeurs absolues, et leurs plages prises en charge.</summary>
public readonly struct AdlxGfxReads
{
    public int          VoltMv       { get; init; }
    public AdlxIntRange VoltRange    { get; init; }
    public int          MaxFreqMhz   { get; init; }
    public AdlxIntRange MaxFreqRange { get; init; }
    public int          MinFreqMhz   { get; init; }
    public AdlxIntRange MinFreqRange { get; init; }
}

/// <summary>Lectures de limite de puissance depuis IADLXManualPowerTuning : limite actuelle (%) et
/// sa plage prise en charge.</summary>
public readonly struct AdlxPowerReads
{
    public int          PowerLimitPct { get; init; }
    public AdlxIntRange PowerRange    { get; init; }
}

/// <summary>Un point d'une courbe de ventilateur GPU.</summary>
public readonly struct AdlxFanPoint
{
    public int TempC    { get; init; }
    public int SpeedPct { get; init; }
}

/// <summary>Lectures de réglage du ventilateur depuis IADLXManualFanTuning : états actuels de la
/// courbe et plages de température / vitesse prises en charge.</summary>
public readonly struct AdlxFanReads
{
    public IReadOnlyList<AdlxFanPoint>? Points     { get; init; }
    public AdlxIntRange                 TempRange  { get; init; }
    public AdlxIntRange                 SpeedRange { get; init; }
}

/// <summary>Une étape du parcours de diagnostic du chemin de lecture GFX d'ADLX (voir
/// <see cref="IAdlxNative.ProbeGfxPath"/>). <paramref name="Name"/> est l'étape,
/// <paramref name="Ok"/> sa réussite, <paramref name="Detail"/> une courte raison lisible : où ADLX s'est arrêté sur du
/// vrai matériel AMD, plutôt que « non disponible ».</summary>
public sealed record GpuProbeStep(string Name, bool Ok, string? Detail = null);

/// <summary>Ce que la carte dédiée AMD rend pendant une partie (IADLXGPUMetrics) : puissance de la carte (à défaut, de la
/// puce), température, point chaud, fréquence graphique, ventilateur. Chaque champ à null = non lu ; jamais zéro.
/// <paramref name="Raison"/> dit pourquoi rien n'a été lu quand c'est le cas.</summary>
public sealed record AdlxMetriques(
    string? Raison = null,
    double? PuissanceW = null,
    double? TemperatureC = null,
    double? PointChaudC = null,
    int? FrequenceMhz = null,
    int? VentilateurTrMin = null)
{
    public static AdlxMetriques RienLu(string raison) => new(Raison: raison);
}

/// <summary>Les réglages AMD lus par ADLX sur la carte dédiée. Chaque champ à null = non lu
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
    // La cible FRTC réglée, lue même quand FRTC est éteint (la rendre exacte après un essai).
    int? FrtcFpsReglee = null,
    // Le mode Vsync du pilote (0 à 3) et Enhanced Sync, qu'allumer la Vsync éteint.
    int? VsyncMode = null,
    bool? EnhancedSyncActif = null,
    // Les réglages Radeon lus seulement : Boost et Image Sharpening de la carte, RSR et AFMF du système. Non pris en
    // charge = null, comme SAM et Anti-Lag : « éteint » ferait proposer de les allumer.
    bool? BoostActif = null,
    bool? ImageSharpeningActif = null,
    bool? RsrActif = null,
    bool? AfmfActif = null)
{
    public static AdlxReglagesLus RienLu(string raison) => new(Raison: raison);

    /// <summary>L'état FRTC en un entier, celui de <c>AdlxTuningKind.FrameRateTarget</c> : allumé = sa cible
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

/// <summary>Un écran vu par le pilote AMD, son nom (celui de l'EDID) et l'état de FreeSync.
/// null = non lu.</summary>
public sealed record AdlxEcranFreeSync(string Nom, bool? Supporte, bool? Actif);

/// <summary>Frontière mince sur ADLX natif. Ne lève jamais : rend false sur tout échec (DLL
/// absente, mauvaise vtable, interface non prise en charge, etc.).</summary>
public interface IAdlxNative
{
    /// <summary>Vrai quand ADLXInitialize a réussi (GPU AMD + pilote Adrenalin présents).</summary>
    bool IsAvailable { get; }

    /// <summary>Lit le réglage graphique du GPU via IADLXManualGraphicsTuning2 (RDNA) pour l'index de GPU donné.
    /// Rend false si l'index est hors plage ou l'interface non prise en charge : une Vega (Tuning1 seulement) n'expose
    /// pas les lectures de décalage et de tension, elle rend false.</summary>
    bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads);

    /// <summary>Lit le réglage de limite de puissance via IADLXManualPowerTuning.
    /// Rend false si l'index est hors plage ou non pris en charge.</summary>
    bool TryReadPower(int gpuIndex, out AdlxPowerReads reads);

    /// <summary>Lit le réglage de courbe du ventilateur via IADLXManualFanTuning.
    /// Rend false si l'index est hors plage ou non pris en charge.</summary>
    bool TryReadFan(int gpuIndex, out AdlxFanReads reads);

    // ── méthodes d'écriture (appelées par le helper élevé, jamais pendant dotnet test) ─────────

    /// <summary>
    /// GET puis SET : l'état GFX actuel capturé dans <paramref name="original"/>, les valeurs validées contre
    /// GetGPUMaxFrequencyRange / GetGPUVoltageRange, puis (emplacements vérifiés contre les en-têtes du SDK ADLX) :
    ///   SetGPUMaxFrequency  emplacement de vtable 8 d'IADLXManualGraphicsTuning2
    ///   SetGPUVoltage       emplacement de vtable 11 d'IADLXManualGraphicsTuning2
    /// Si SetGPUVoltage échoue après SetGPUMaxFrequency, MaxFreq est remise au mieux à l'original. False si indisponible,
    /// hors RDNA, hors plage ou sur toute panne ; ne lève jamais.
    /// </summary>
    bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original);

    /// <summary>
    /// GET puis SET : la limite actuelle capturée dans <paramref name="original"/>, la valeur validée contre
    /// GetPowerLimitRange, puis :
    ///   SetPowerLimit  emplacement de vtable 5 d'IADLXManualPowerTuning (vérifié contre les en-têtes du SDK ADLX)
    /// False si indisponible, non pris en charge, hors plage ou sur toute panne ; ne lève jamais.
    /// </summary>
    bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original);

    // ── Diagnostics (lecture seule ; n'écrit jamais, ne lève jamais) ──────────────────

    /// <summary>Parcourt pas à pas le chemin de lecture GFX (DLL / init → GetGPUs → taille / index de
    /// la liste → GetGPU → GetGPUTuningServices → IsSupportedManualGFXTuning → GetManualGFXTuning →
    /// QueryInterface(Tuning2) → ReadGfxV2), l'issue de chaque étape notée, arrêt au premier échec avec sa raison.
    /// Lecture seule ; ne lève jamais.</summary>
    IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex);

    /// <summary>SAM, Anti-Lag, FRTC, Chill et température de la carte dédiée. Lecture seule ; ne lève
    /// jamais. Par défaut (doublures qui ne la portent pas) : rien lu.</summary>
    AdlxReglagesLus LireReglages() => AdlxReglagesLus.RienLu("This seam does not read ADLX settings.");

    /// <summary>Puissance, températures, fréquence et ventilateur de la carte dédiée, à l'instant. Lecture seule ; ne lève
    /// jamais. Par défaut (doublures qui ne la portent pas) : rien lu.</summary>
    AdlxMetriques LireMetriques() => AdlxMetriques.RienLu("This seam does not read ADLX metrics.");

    /// <summary>Allume ou éteint Anti-Lag (le classique ; le niveau Anti-Lag Next n'est jamais
    /// touché) sur la carte dédiée, celle que <see cref="LireReglages"/> lit. Rend <c>null</c> si écrit, sinon la
    /// raison ; ne lève jamais. Par défaut (doublures qui ne la portent pas) : refusé.</summary>
    string? EcrireAntiLag(bool actif) => "This seam does not write Anti-Lag.";

    /// <summary>Remet tout le réglage de la carte à l'usine (IADLXGPUTuningServices.ResetToFactory) : fréquence, tension,
    /// mémoire, ventilateur et puissance. Rend null si fait, sinon la raison.</summary>
    string? RemettreReglageUsine(int gpuIndex) => "This seam does not reset GPU tuning.";

    /// <summary>Le réglage de la carte est-il à l'usine (IADLXGPUTuningServices.IsAtFactory) ? null = non lu.</summary>
    bool? EstALUsine(int gpuIndex) => null;

    /// <summary>Pose FRTC sur la carte dédiée. <paramref name="code"/> &gt; 0 = allumé à cette cible ;
    /// &lt; 0 = éteint, cible rendue à <c>-code</c> ; 0 refusé. La cible doit tenir dans GetFPSRange. Rend <c>null</c>
    /// si écrit, sinon la raison ; ne lève jamais.</summary>
    string? EcrireFrtc(int code) => "This seam does not write FRTC.";

    /// <summary>Pose le mode Vsync du pilote (0 à 3, ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE) sur la
    /// carte dédiée. Rend <c>null</c> si écrit, sinon la raison ; ne lève jamais.</summary>
    string? EcrireVsync(int mode) => "This seam does not write Vsync.";

    /// <summary>Pose la même vitesse (en %) à chaque état de la courbe du ventilateur de la carte dédiée et coupe le Zero
    /// RPM. Rend <c>null</c> si posé et relu, sinon la raison ; ne lève jamais.</summary>
    string? PoserVentilateurFixe(int pourcent) => "This seam does not write the fan.";

    /// <summary>Remet la courbe et le Zero RPM par défaut de la carte dédiée. Rend <c>null</c> si remis et relu, sinon la
    /// raison ; ne lève jamais.</summary>
    string? RendreVentilateurAuto() => "This seam does not write the fan.";

    /// <summary>La température de la carte dédiée en °C ; null si elle ne se lit pas.</summary>
    double? LireTemperatureCarte() => null;

    /// <summary>A-FS1 : FreeSync de chaque écran branché sur la carte AMD, lu au pilote. Vide = rien lu.</summary>
    IReadOnlyList<AdlxEcranFreeSync> LireFreeSync() => Array.Empty<AdlxEcranFreeSync>();
}
