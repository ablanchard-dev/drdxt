namespace DrDxt.Core;

// --- P1 Measurement ---

/// <summary>Which component paced a session, read from GPU-busy vs frame time. Intel PresentMon's
/// GPU-Busy methodology: GPU-bound ⟺ GPU busy tracks frame time closely; CPU/other-bound ⟺ a large
/// gap (the GPU finished and waited on the CPU, engine or presentation path). Indeterminate when GPU
/// busy wasn't measured or the machine sits between the two — never a guessed limiter.
/// Source (concept): Intel PresentMon GPU-Busy — https://presentmon.com/ .</summary>
public enum Bottleneck { Indeterminate, GpuBound, CpuOrOtherBound }

public record BenchmarkResult(
    double AvgFps,
    double P1LowFps,
    double P01LowFps,
    IReadOnlyList<double> FrameTimesMs,
    double? EstimatedPcLatencyMs,
    double MarginOfError,
    int Runs,
    double? MeanGpuBusyMs = null,
    // ── L1.1 — les grandeurs que le classifieur demande ────────────────────────────────────
    // La note de conception du classifieur (voir `BottleneckClassifier`) en exige 8 ; ce contrat
    // n'en portait qu'une (MeanGpuBusyMs). Le produit ne pouvait donc distinguer que
    // « GPU-bound / pas GPU-bound » et vendait les memes leviers a tout le monde. Toutes
    // optionnelles et en queue : null = pas mesuree, jamais zero -- la distinction porte tout
    // l'etat UNKNOWN_PARTIAL, qui interdit au produit de reclamer un gain.
    //
    // Ce sont des AGREGATS et pas des series : les ratios se calculent frame par frame la ou
    // les series sont paralleles (BenchmarkRunner), puis on garde la mediane. Les recalculer
    // ici sur des moyennes de moyennes donnerait un autre chiffre, et un plus faux.

    /// <summary>Mediane par frame de GpuWait / frametime. Une attente GPU elevee = le GPU
    /// poireaute pendant que l'app ou le CPU donne le tempo. C'est la grandeur qui separe
    /// « CPU-bound » de « simplement pas GPU-bound ».</summary>
    double? GpuWaitRatio = null,

    /// <summary>Mediane par frame de CpuBusy / frametime. Travail CPU de l'APPLICATION sur la
    /// frame, pas la charge CPU de la machine (PresentMon le dit explicitement) : les confondre
    /// ferait accuser le CPU d'un systeme charge par autre chose.</summary>
    double? CpuBusyRatio = null,

    /// <summary>p95 du temps passe dans Present(). La signature d'un blocage vsync / cap /
    /// compositeur : la frame est prete et on attend.</summary>
    double? PresentStallMsP95 = null,

    /// <summary>p95 de la latence render→present. Le p95 et non la moyenne : un a-coup de
    /// latence vit precisement dans la queue que la moyenne noie.</summary>
    double? RenderPresentLatencyMsP95 = null,

    /// <summary>p95 de la valeur absolue de l'erreur d'animation. Le signal de cadence : une
    /// moyenne de 140 FPS peut etre desagreable a jouer, et c'est la seule grandeur qui le
    /// dit.</summary>
    double? AnimationErrorMsP95 = null,

    /// <summary>Octets valides / limite de validation de l'OS, dans [0, 1]. Ne vient pas de
    /// PresentMon mais d'un compteur systeme : c'est la seule grandeur du classifieur qui
    /// parle de fiabilite (gels, OOM) et pas de FPS, et Microsoft documente le seuil de
    /// 90 %.</summary>
    double? CommitPressure = null,

    /// <summary>
    /// La part de trames que l'hygiene a gardees, dans [0, 1] — l'exposition reelle de la mesure.
    ///
    /// Sans elle, deux passes A/B peuvent avoir vecu des choses completement differentes sans
    /// que rien ne le dise. Mesure du 06/09/2026 : une charge GPU concurrente decale le niveau de
    /// **−23,9 %** sans faire exploser la variance — un A/B dont les deux moities n'ont pas la
    /// meme charge de fond compare deux machines, et le verdict sort sans le moindre signe
    /// d'erreur. Le meme piege vaut pour un alt-tab : l'hygiene retire les secondes hors focus, et
    /// une passe amputee de moitie n'est pas comparable a une passe intacte.
    ///
    /// Repere mesure le 07/09/2026 : sur les **2 170 secondes** de jeu reel conservees dans
    /// `tools/p3`, l'hygiene retire **0 %**. Une valeur nettement sous 1,0 signale donc du
    /// non-gameplay dans la passe, pas une machine lente.
    ///
    /// <c>null</c> = l'hygiene n'a pas rendu compte (rejeu hors ligne, mesure anterieure).
    /// Unknown ne deduit rien : ni « comparable », ni « invalide ».
    /// </summary>
    double? HygieneKeptFraction = null,

    /// <summary>
    /// La part du temps ou le jeu etait reellement au premier plan, dans [0, 1] — OBSERVEE.
    ///
    /// Jusqu'ici l'alt-tab etait DEVINE a partir de la regularite des frametimes : une seconde
    /// lente et reguliere ressemble a un plafond hors focus. L'inference se trompe dans les deux
    /// sens — un jeu qui rame regulierement passe pour un alt-tab, un alt-tab irregulier passe
    /// pour du jeu. Ici on a regarde.
    ///
    /// Le focus ne dit rien de la performance : il dit si la mesure porte sur le jeu. Une passe
    /// entierement jouee reste comparable, que le PC y ait ete brillant ou catastrophique — c'est
    /// justement ce qu'on veut mesurer. **On ne nettoie jamais les mauvaises performances du
    /// joueur.**
    ///
    /// <c>null</c> = le focus n'a pas ete observe (rejeu hors ligne, sonde absente). Unknown ne
    /// condamne pas.
    /// </summary>
    double? ForegroundFraction = null,

    /// <summary>
    /// Le p95 des frametimes, en millisecondes — la grandeur de queue qui sait decider.
    ///
    /// Le produit n'avait que le 1 % low pour parler de fluidite, et le 1 % low resout mal :
    /// en session live il annoncait +/-132 %. Sur le corpus des 8 captures Warzone, en fenetres
    /// NON chevauchantes, son ecart maximal sous no-op va jusqu'a 43,7 % sur la partie la plus
    /// agitee, contre 15,0 % pour la moyenne.
    ///
    /// Le p95 frametime est la meme famille — la queue lente — mais il repose sur 5 % des frames
    /// au lieu de 1 %, donc il ne se fait pas dicter sa valeur par une poignee d'a-coups. En
    /// leave-one-capture-out, il est choisi sur 7 holdouts sur 8 sans jamais voir la partie sur
    /// laquelle il est evalue.
    ///
    /// Il n'est pas « la fluidite » : il decrit les 5 % de frames les plus lentes, pas le pire
    /// centile. Sur une deformation qui ajoute un a-coup toutes les 60 frames, il rend +1,6 %
    /// pendant que le 1 % low prouve une regression de -65,6 %. Les deux sont necessaires.
    ///
    /// Il se lit A L'ENVERS : plus PETIT = mieux. Voir <see cref="GameMetricSet.Valeur"/>, qui
    /// le convertit en FPS equivalents avant toute comparaison.
    ///
    /// Le p99 a ete essaye et REJETE au meme banc : systematiquement surconfiant (il annonce
    /// 9,2 % pour un ecart p95 observe de 15,3 %), il fabriquerait des verdicts.
    /// </summary>
    double? P95FrametimeMs = null,

    /// <summary>Ce que le materiel a fait pendant la passe : puissance mediane, temperature max.
    /// <c>null</c> = aucune sonde branchee. C'est une OBSERVATION, jamais une cause : le
    /// produit a le droit de dire « le GPU etait a 68 °C pendant les fenetres mesurees », pas
    /// d'en deduire que la temperature coute des FPS.</summary>
    HardwareObservation? Materiel = null,

    /// <summary>La part du temps ECOULE pendant laquelle le GPU a travaille, dans [0, 1], APPARIEE
    /// trame par trame : somme des GPU-busy sur somme des temps de trame, sur les seules trames ou
    /// les deux existent.
    ///
    /// Optionnelle et en queue : <c>null</c> = la passe n'a pas pu l'apparier (serie GPU
    /// desynchronisee, mesure enregistree avant que ce champ existe). <see cref="GpuBusyPercent"/>
    /// retombe alors sur l'ancienne approximation, et la nomme.</summary>
    double? GpuBusyShare = null,

    /// <summary>Combien D'ECHANTILLONS portent <see cref="AnimationErrorMsP95"/>.
    ///
    /// Sans lui, la seule facon de juger si ce p95 merite son nom etait de compter les
    /// FRAMETIMES — une autre serie. Une passe dont l'erreur d'animation est absente ou
    /// desynchronisee ne contribue pas au percentile, et le compte des trames ne le dit pas : on
    /// validait un p95 tire de huit points en en lisant mille. Meme faute que deux moyennes prises
    /// sur des trames differentes.
    ///
    /// <c>null</c> = compte inconnu (mesure enregistree avant ce champ). Inconnu n'est pas
    /// « assez » : l'affichage se tait.</summary>
    int? AnimationErrorSamples = null,

    /// <summary>Combien D'ECHANTILLONS portent <see cref="PresentStallMsP95"/>.
    ///
    /// Meme raison que <see cref="AnimationErrorSamples"/>, appliquee d'entree cette fois : ce
    /// p95 vient de la serie `InPresentApi`, pas des trames. Juger son interpretabilite au nombre
    /// de trames reviendrait a valider un percentile sur une population qui n'est pas la sienne —
    /// la faute payee deux fois dans cette boucle. <c>null</c> = compte inconnu : on se tait.</summary>
    int? PresentStallSamples = null,
    /// <summary>24/09/2026 — qui a compte les images. <c>null</c> = PresentMon (le banc historique) ;
    /// « DXGI » = le compteur de repli, qui ne voit ni la latence ni le focus.</summary>
    string? Instrument = null,
    /// <summary>25/09/2026 — quel processus a ete mesure. Un rapport lu le 25/09 donnait 99 FPS sur un
    /// scan que l'on croyait sans jeu : c'etait JeuB, au premier plan. Sans le nom, le chiffre ne
    /// se verifie pas. <c>null</c> = inconnu.</summary>
    string? ProcessusMesure = null,

    /// <summary>26/09/2026 : la marge des 1 % lows, tiree de leur dispersion ENTRE passes. L'A/B
    /// jugeait les lows avec la marge des FPS moyens, alors que les lows varient bien plus d'une
    /// passe a l'autre : un « gain » ou une « regression » des lows pouvait etre du bruit, et une
    /// regression annule le reglage. +inf par defaut : un resultat qui ne l'a pas calculee ne
    /// conclut jamais sur les lows.</summary>
    double P1LowMarginOfError = double.PositiveInfinity,

    /// <summary>28/09/2026 : la marge des 0,1 % lows, meme calcul. Le chiffre le plus fragile du rapport
    /// sortait seul sans marge. Informative, jamais decisionnelle (voir <c>GameObjective</c>).</summary>
    double P01LowMarginOfError = double.PositiveInfinity,
    /// <summary>B4 (10/10/2026) : médiane de la latence d'affichage de PresentMon (début de l'image → écran), en ms ;
    /// null quand la série n'a pas été livrée. Le chiffre sur lequel l'essai en alternance juge un levier de latence.</summary>
    double? DisplayLatencyMsMedian = null)
{
    /// <summary>GPU-busy as a % of frame time: the CPU-bound (≪ 100) vs GPU-bound (≈ 100) signal.
    /// Null when GPU busy was not measured or the frame rate is unknown — never a fabricated 0.
    ///
    /// Corrige LE 10/09/2026. Cette valeur se calculait « moyenne des GPU-busy ÷ moyenne des
    /// temps de trame », et les deux moyennes ne portaient pas forcement sur les MEMES trames :
    /// une serie GPU desynchronisee etait quand meme moyennee, et un GPU-busy a zero — mesure
    /// valide : le GPU n'a rien fait sur cette trame — etait ecarte du haut sans l'etre du bas.
    /// Le verdict de goulot du produit et le tableau de bord en dependaient deja.
    ///
    /// <see cref="GpuBusyShare"/> est desormais la source : une part APPARIEE, somme sur somme,
    /// des seules trames ou les deux grandeurs existent. L'ancienne derivation reste en REPLI
    /// nomme, pour les mesures enregistrees avant que l'appariement existe (rejeu de journal) —
    /// c'est une approximation, et elle est ecrite comme telle plutot que supprimee en silence.
    ///
    /// Et le repli ne doit jamais se rallumer sur une capture neuve. L'invariant qui le garantit
    /// vit dans `BenchmarkRunner` : une passe n'entre au numerateur que si ses trames entrent au
    /// denominateur, donc une mesure qui a lu du GPU publie toujours sa part appariee. Sans cet
    /// invariant, cinq passes dont une seule desynchronisee suffisaient a rendre la part
    /// incalculable et a rebrancher le melange de populations sur une mesure du jour.
    /// Verrouille par BenchmarkRunnerGpuOccupancyTests.</summary>
    public double? GpuBusyPercent =>
        GpuBusyShare is { } part ? part * 100.0
        : MeanGpuBusyMs is { } busy && AvgFps > 0 ? busy * AvgFps / 10.0
        : null;

    /// <summary>GPU-busy % at/above which the GPU paced nearly the whole frame → GPU-bound. Intel
    /// PresentMon defines the CONCEPT (a small GPU-busy↔frame-time gap = GPU-bound) but publishes NO
    /// numeric cutoff; 90 % (≤10 % off-GPU) is DrDXT's conservative default, not a sourced constant.
    /// calibration knob: raise toward 95 to be stricter before claiming GPU-bound.</summary>
    public const double GpuBoundBusyPercent = 90.0;

    /// <summary>GPU-busy % at/below which a meaningful share of the frame ran off-GPU → the CPU or
    /// another subsystem paced it. DrDXT default (≥25 % off-GPU). The 75–90 band stays Indeterminate
    /// on purpose so we never over-claim either limiter. calibration knob.</summary>
    public const double CpuBoundBusyPercent = 75.0;

    /// <summary>The session's limiter (<see cref="Core.Bottleneck"/>), from <see cref="GpuBusyPercent"/>.
    /// Indeterminate when GPU busy is unmeasured — the honest degraded state, never a fabricated
    /// GpuBound. Lets the reco layer avoid selling a GPU tweak as an FPS win on a CPU-bound machine.</summary>
    public Bottleneck Bottleneck =>
        GpuBusyPercent is not { } pct ? Bottleneck.Indeterminate
        : pct >= GpuBoundBusyPercent ? Bottleneck.GpuBound
        : pct <= CpuBoundBusyPercent ? Bottleneck.CpuOrOtherBound
        : Bottleneck.Indeterminate;

    /// <summary>CapFrameX default "stuttering factor": a frame counts as a stutter when its frame
    /// time exceeds 2.5× the average. Source: CapFrameX (reference frametime-analysis tool).</summary>
    public const double StutterFactor = 2.5;

    /// <summary>Share of play-TIME (0–100 %) spent in stutter frames — frames whose frame time
    /// exceeds <see cref="StutterFactor"/>× the mean. Mirrors CapFrameX's stuttering %, and is the
    /// honest MEASURED-stability signal (100 − this = % of time that was smooth). Null when no valid
    /// frame was captured — never a fabricated 0. Invalid frames (≤ 0 / NaN / ∞) are filtered like
    /// the rest of the frametime maths.
    /// heuristic: thresholds against the global mean, not CapFrameX's moving average — defensible for
    /// the fixed-scene A/B protocol (the project's measurement-proof note, unpublished, §6); switch to a windowed mean if scene
    /// variance within a capture starts to matter.</summary>
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
    /// Coefficient de variation des frametimes (ecart-type / moyenne) — le proxy de cadence
    /// Normalise : il met une machine a 60 FPS et une a 240 FPS sur la meme echelle, ce qu'un
    /// ecart-type brut ne fait pas. Derive des frametimes deja portees ici, donc pas de
    /// parametre de plus. Null avec moins de deux frames valides : une seule frame n'a pas de
    /// dispersion, et rendre 0 dirait « parfaitement regulier » sur une absence de mesure.
    /// </summary>
    public double? FrameMsCv
    {
        get
        {
            var ft = FrameTimesMs.Where(x => x > 0 && double.IsFinite(x)).ToList();
            if (ft.Count < 2) return null;
            var mean = ft.Average();
            if (mean <= 0) return null;
            var variance = ft.Sum(x => (x - mean) * (x - mean)) / (ft.Count - 1);
            return Math.Sqrt(variance) / mean;
        }
    }

    /// <summary>
    /// Rapport p99 / p50 des frametimes — l'autre lecture de la cadence, celle des a-coups
    /// rares : le CV peut rester sage pendant que 1 % des frames partent au double. Null quand
    /// il n'y a aucune frame valide.
    /// </summary>
    public double? FrameMsP99OverP50
    {
        get
        {
            var p50 = FrameMsP50;
            var p99 = FramePercentileMs(0.99);
            return p50 is > 0 && p99 is { } high ? high / p50.Value : null;
        }
    }

    /// <summary>
    /// Frametime median (ms). La mesure D'ECHELLE de la machine : « 14 ms de latence », c'est
    /// beaucoup a 143 FPS et normal a 60. Tout seuil de latence exprime en millisecondes fixes
    /// serait juste sur une machine et faux sur toutes les autres — c'est contre ca qu'il existe.
    /// </summary>
    public double? FrameMsP50 => FramePercentileMs(0.50);

    private double? FramePercentileMs(double fraction)
    {
        var ft = FrameTimesMs.Where(x => x > 0 && double.IsFinite(x)).OrderBy(x => x).ToList();
        if (ft.Count == 0) return null;
        return ft[Math.Clamp((int)Math.Ceiling(fraction * ft.Count) - 1, 0, ft.Count - 1)];
    }

    /// <summary>
    /// Le ratio GPU-busy en [0, 1], derive de <see cref="GpuBusyPercent"/> — la forme que la
    /// table de decision du classifieur utilise (0,92 / 0,80 / 0,75 / 0,60). Une seule source :
    /// changer le calcul du pourcentage change le ratio, ils ne peuvent pas diverger.
    /// </summary>
    public double? GpuBusyRatio => GpuBusyPercent is { } pct ? pct / 100.0 : null;
}

public interface IBenchmarkRunner
{
    Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct);
}

public record AbVerdict(BenchmarkResult Before, BenchmarkResult After, bool IsMeasurableGain, string SummaryKey)
{
    /// <summary>The baseline session's limiter (M8). Classified from <see cref="Before"/> — the
    /// unmodified machine's natural limiter — so the reco layer can caveat a GPU-lever gain that
    /// landed on a CPU/other-bound machine instead of selling it as game FPS.</summary>
    public Bottleneck Bottleneck => Before.Bottleneck;
}
