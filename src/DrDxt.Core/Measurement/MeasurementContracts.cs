namespace DrDxt.Core;

// --- Mesure ---

public record BenchmarkResult(
    double AvgFps,
    double P1LowFps,
    double P01LowFps,
    IReadOnlyList<double> FrameTimesMs,
    double? EstimatedPcLatencyMs,
    double MarginOfError,
    int Runs,
    double? MeanGpuBusyMs = null,
    // Les grandeurs que le classifieur demande (8, voir `BottleneckClassifier`) : sans elles on ne distingue que
    // « GPU-bound / pas GPU-bound ». Toutes optionnelles et en queue : null = pas mesuree, jamais zero (c'est l'etat
    // UNKNOWN_PARTIAL, qui interdit de reclamer un gain). Des agregats : les ratios se calculent frame par frame dans
    // BenchmarkRunner, puis mediane ; une moyenne de moyennes donnerait un chiffre plus faux.

    /// <summary>Mediane par frame de GpuWait / frametime : le GPU attend l'app ou le CPU. Separe « CPU-bound » de
    /// « simplement pas GPU-bound ».</summary>
    double? GpuWaitRatio = null,

    /// <summary>Mediane par frame de CpuBusy / frametime : travail CPU de l'application sur la frame, pas la charge CPU
    /// de la machine (PresentMon le precise) ; les confondre accuserait le CPU d'un systeme charge par autre chose.</summary>
    double? CpuBusyRatio = null,

    /// <summary>p95 du temps passe dans Present() : la signature d'un blocage vsync / cap / compositeur.</summary>
    double? PresentStallMsP95 = null,

    /// <summary>p95 de la latence render→present : un a-coup vit dans la queue que la moyenne noie.</summary>
    double? RenderPresentLatencyMsP95 = null,

    /// <summary>p95 de la valeur absolue de l'erreur d'animation : la seule grandeur qui dit qu'une moyenne de
    /// 140 FPS peut etre desagreable a jouer.</summary>
    double? AnimationErrorMsP95 = null,

    /// <summary>Octets valides / limite de validation de l'OS, dans [0, 1], d'un compteur systeme : la seule grandeur
    /// de fiabilite (gels, OOM) du classifieur ; Microsoft documente le seuil de 90 %.</summary>
    double? CommitPressure = null,

    /// <summary>
    /// La part de trames que l'hygiene a gardees, dans [0, 1] : l'exposition reelle de la mesure. Mesure du 06/09/2026 :
    /// une charge GPU concurrente decale le niveau de **−23,9 %** sans faire exploser la variance ; un A/B dont les
    /// moities n'ont pas la meme charge de fond compare deux machines sans signe d'erreur. Idem pour un alt-tab : une
    /// passe amputee de moitie n'est pas comparable a une passe intacte.
    /// Repere mesure le 07/09/2026 : sur les **2 170 secondes** de jeu reel de `tools/p3`, l'hygiene retire **0 %** ; nettement
    /// sous 1,0 = du non-gameplay dans la passe, pas une machine lente.
    /// <c>null</c> = l'hygiene n'a pas rendu compte (rejeu hors ligne, mesure anterieure) : ni « comparable », ni « invalide ».
    /// </summary>
    double? HygieneKeptFraction = null,

    /// <summary>
    /// La part du temps ou le jeu etait reellement au premier plan, dans [0, 1], observee et non devinee de la
    /// regularite des frametimes (l'inference se trompe dans les deux sens : un jeu qui rame regulierement passe pour
    /// un alt-tab, un alt-tab irregulier pour du jeu).
    /// Le focus dit si la mesure porte sur le jeu, rien de la performance : une passe entierement jouee reste
    /// comparable. **On ne nettoie jamais les mauvaises performances du joueur.**
    /// <c>null</c> = focus non observe (rejeu hors ligne, sonde absente) : ne condamne pas.
    /// </summary>
    double? ForegroundFraction = null,

    /// <summary>
    /// Le p95 des frametimes, en ms : la grandeur de queue qui sait decider. Le 1 % low resout mal (+/-132 % en session
    /// live ; sur les 8 captures Warzone en fenetres disjointes, ecart maximal sous no-op de 43,7 % contre 15,0 % pour
    /// la moyenne). Le p95 repose sur 5 % des frames, pas 1 % : une poignee d'a-coups ne dicte pas sa valeur ; en
    /// leave-one-capture-out il est choisi sur 7 holdouts sur 8.
    /// Il ne remplace pas le 1 % low : avec un a-coup toutes les 60 frames, il rend +1,6 % quand le 1 % low prouve
    /// -65,6 %. Plus petit = mieux (<see cref="GameMetricSet.Valeur"/> le convertit en FPS equivalents).
    /// Le p99, essaye au meme banc, est surconfiant (9,2 % annonce pour un ecart p95 observe de 15,3 %) : rejete.
    /// </summary>
    double? P95FrametimeMs = null,

    /// <summary>Ce que le materiel a fait pendant la passe : puissance mediane, temperature max ; <c>null</c> = aucune
    /// sonde. Une observation, jamais une cause : « le GPU etait a 68 °C », pas « la temperature coute des FPS ».</summary>
    HardwareObservation? Materiel = null,

    /// <summary>La part du temps ou le GPU a travaille, dans [0, 1], appariee trame par trame : somme des GPU-busy sur
    /// somme des temps de trame, sur les seules trames ou les deux existent. <c>null</c> = non appariee (serie GPU
    /// desynchronisee, mesure plus ancienne que ce champ) : <see cref="GpuBusyPercent"/> retombe sur l'approximation,
    /// et la nomme.</summary>
    double? GpuBusyShare = null,

    /// <summary>Combien d'echantillons portent <see cref="AnimationErrorMsP95"/>. Le compte des trames ne le dit pas :
    /// une passe sans erreur d'animation n'y contribue pas, et on validerait un p95 de huit points en en lisant mille.
    /// <c>null</c> = inconnu (mesure plus ancienne que ce champ) : l'affichage se tait.</summary>
    int? AnimationErrorSamples = null,

    /// <summary>Combien d'echantillons portent <see cref="PresentStallMsP95"/>, qui vient de la serie `InPresentApi`,
    /// pas des trames (meme raison que <see cref="AnimationErrorSamples"/>). <c>null</c> = inconnu : on se tait.</summary>
    int? PresentStallSamples = null,
    /// <summary>Qui a compte les images. <c>null</c> = PresentMon (le banc historique) ;
    /// « DXGI » = le compteur de repli, qui ne voit ni la latence ni le focus.</summary>
    string? Instrument = null,
    /// <summary>Quel processus a ete mesure : un rapport lu le 25/09 donnait 99 FPS sur un scan cru sans jeu (c'etait
    /// JeuB, au premier plan). Sans le nom, le chiffre ne se verifie pas. <c>null</c> = inconnu.</summary>
    string? ProcessusMesure = null,

    /// <summary>La marge des 1 % lows, tiree de leur dispersion entre passes : ils varient bien plus que les FPS
    /// moyens, et un faux « gain » ou une fausse regression (qui annule le reglage) serait du bruit. +inf par defaut :
    /// sans elle, on ne conclut jamais sur les lows.</summary>
    double P1LowMarginOfError = double.PositiveInfinity,

    /// <summary>La marge des 0,1 % lows, meme calcul. Informative, jamais decisionnelle
    /// (voir <c>GameMetrics.P01LowFps</c>).</summary>
    double P01LowMarginOfError = double.PositiveInfinity,
    /// <summary>Médiane de la latence d'affichage de PresentMon (début de l'image → écran), en ms ;
    /// null quand la série n'a pas été livrée. Le chiffre sur lequel l'essai en alternance juge un levier de latence.</summary>
    double? DisplayLatencyMsMedian = null,
    /// <summary>Latence PC de PresentMon (entrée → écran), médiane et p95, en ms ; null quand le jeu ne pose pas les
    /// marqueurs de latence ou que la série n'a pas été livrée.</summary>
    double? PcLatencyMsMedian = null,
    double? PcLatencyMsP95 = null,
    /// <summary>La part des images recopiées par le compositeur de Windows (0-1), sur celles dont le mode est connu ;
    /// null = aucun mode connu.</summary>
    double? PartImagesComposees = null)
{
    /// <summary>GPU-busy en % du temps de trame : le signal CPU-bound (≪ 100) contre GPU-bound (≈ 100).
    /// Null si le GPU busy n'est pas mesuré ou si la cadence est inconnue, jamais un 0 inventé.
    /// Source : <see cref="GpuBusyShare"/>, appariee. « Moyenne des GPU-busy ÷ moyenne des temps de trame » melange des
    /// trames differentes (une serie desynchronisee moyennee quand meme, un GPU-busy a zero ecarte du haut sans l'etre
    /// du bas) : il ne reste qu'en repli nomme, pour les mesures sans appariement (rejeu de journal).
    /// Le repli ne se rallume jamais sur une capture neuve : dans `BenchmarkRunner`, une passe n'entre au numerateur
    /// que si ses trames entrent au denominateur (sinon une passe desynchronisee sur cinq rendrait la part
    /// incalculable). Verrouille par BenchmarkRunnerGpuOccupancyTests.</summary>
    public double? GpuBusyPercent =>
        GpuBusyShare is { } part ? part * 100.0
        : MeanGpuBusyMs is { } busy && AvgFps > 0 ? busy * AvgFps / 10.0
        : null;

    /// <summary>« Stuttering factor » par défaut de CapFrameX : une trame est un à-coup quand son
    /// temps dépasse 2,5× la moyenne. Source : CapFrameX (outil de référence d'analyse des frametimes).</summary>
    public const double StutterFactor = 2.5;

    /// <summary>Part du temps de jeu (0–100 %) passée dans des trames à-coups, dont le temps dépasse
    /// <see cref="StutterFactor"/>× la moyenne. Calqué sur le stuttering % de CapFrameX : c'est le
    /// signal honnête de stabilité mesurée (100 − ceci = % du temps fluide). Null sans trame valide (≤ 0 / NaN / ∞
    /// filtrées), jamais un 0 inventé.
    /// Seuil sur la moyenne globale, pas glissante comme CapFrameX : défendable pour l'A/B à scène fixe (note de preuve
    /// de mesure, non publiée) ; passer à une moyenne par fenêtre si la variance de scène devient gênante.</summary>
    public double? StutterTimePercent
    {
        get
        {
            var ft = FrameTimesMs.Where(x => x > 0 && double.IsFinite(x)).ToList();
            if (ft.Count == 0) return null;
            var threshold = ft.Average() * StutterFactor;
            var total = ft.Sum();
            return total <= 0 ? null : ft.Where(x => x > threshold).Sum() / total * 100.0;
        }
    }

    /// <summary>
    /// Coefficient de variation de la cadence d'une image a la suivante : sqrt(somme des (x[i] - x[i-1])^2 / (2(n-1)))
    /// sur la moyenne. Une scene qui passe de 150 a 100 i/s ne le fait pas monter (l'ecart-type global y lirait une
    /// cadence cassee) ; normalise, il met 60 et 240 FPS sur la meme echelle. Null sous deux frames valides : 0 dirait
    /// « parfaitement regulier » sur une absence de mesure.
    /// </summary>
    public double? FrameMsCv
    {
        get
        {
            var ft = FrameTimesMs.Where(x => x > 0 && double.IsFinite(x)).ToList();
            if (ft.Count < 2) return null;
            var mean = ft.Average();
            if (mean <= 0) return null;
            var sommeDesSauts = 0.0;
            for (var i = 1; i < ft.Count; i++) sommeDesSauts += (ft[i] - ft[i - 1]) * (ft[i] - ft[i - 1]);
            return Math.Sqrt(sommeDesSauts / (2.0 * (ft.Count - 1))) / mean;
        }
    }

    /// <summary>Rapport p99 / p50 des frametimes : les a-coups rares, que le CV ne voit pas quand 1 % des frames partent
    /// au double. Null sans frame valide.</summary>
    public double? FrameMsP99OverP50
    {
        get
        {
            var p50 = FrameMsP50;
            var p99 = FramePercentileMs(0.99);
            return p50 is > 0 && p99 is { } high ? high / p50.Value : null;
        }
    }

    /// <summary>Frametime median (ms), l'echelle de la machine : « 14 ms de latence » est beaucoup a 143 FPS et normal
    /// a 60 ; un seuil en millisecondes fixes ne serait juste que sur une machine.</summary>
    public double? FrameMsP50 => FramePercentileMs(0.50);

    private double? FramePercentileMs(double fraction)
    {
        var ft = FrameTimesMs.Where(x => x > 0 && double.IsFinite(x)).OrderBy(x => x).ToList();
        if (ft.Count == 0) return null;
        return ft[Math.Clamp((int)Math.Ceiling(fraction * ft.Count) - 1, 0, ft.Count - 1)];
    }

    /// <summary>Le ratio GPU-busy en [0, 1], derive de <see cref="GpuBusyPercent"/> (une seule source) : la forme de la
    /// table de decision du classifieur (0,92 / 0,80 / 0,75 / 0,60).</summary>
    public double? GpuBusyRatio => GpuBusyPercent is { } pct ? pct / 100.0 : null;
}

public interface IBenchmarkRunner
{
    Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct);
}

public record AbVerdict(BenchmarkResult Before, BenchmarkResult After, bool IsMeasurableGain, string SummaryKey);
