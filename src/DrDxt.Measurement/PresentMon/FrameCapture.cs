namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// One capture's per-frame series. <see cref="FrameTimesMs"/> drives FPS and the lows;
/// <see cref="RenderPresentLatencyMs"/> is the present→display (render-present) portion of the
/// pipeline only — not system / click-to-photon latency (which also needs the input portion via
/// Reflex markers and the display scanout). <see cref="GpuBusyMs"/> is how long the GPU actually
/// worked on each frame (≤ the frame time); GpuBusy/FrameTime reveals whether a frame is GPU-bound
/// (≈1) or CPU-bound (≪1). All informative, never a score nor an auto-revert trigger. A series is
/// empty when its metric is unavailable. The series are parallel — one entry per captured frame.
///
/// L1.1 — les quatre series ajoutees. Le classifieur de goulot
/// (note de conception, voir `BottleneckClassifier`) demande 8 grandeurs ; le code
/// n'en produisait qu'une. Les champs existaient cote PresentMon et ne remontaient nulle part,
/// donc aucun etat au-dela de « GPU-bound / pas GPU-bound » n'etait calculable. Optionnels et en
/// queue : toutes les constructions existantes restent valides, et une serie vide veut dire
/// « pas mesuree », jamais « zero ».
/// </summary>
/// <param name="GpuWaitMs">
/// PM_METRIC_GPU_WAIT — le temps ou le GPU est INACTIF alors qu'il travaille sur la frame. Une
/// attente elevee dit que quelque chose d'autre (l'app, le CPU) donne le tempo. C'est la grandeur
/// qui separe « CPU-bound » de « simplement pas GPU-bound ».
/// </param>
/// <param name="CpuBusyMs">
/// PM_METRIC_CPU_BUSY — le travail CPU de l'APPLICATION sur la frame. Ce n'est pas la charge
/// CPU de la machine : la doc PresentMon le dit explicitement, et confondre les deux ferait
/// accuser le CPU d'un systeme charge par autre chose.
/// </param>
/// <param name="InPresentApiMs">
/// PM_METRIC_IN_PRESENT_API — le temps passe dans l'appel Present(). C'est la signature d'un
/// blocage vsync / cap / compositeur : la frame est prete et on attend.
/// </param>
/// <param name="AnimationErrorMs">
/// PM_METRIC_ANIMATION_ERROR — l'ecart entre le temps simule et le temps affiche. Le p95 de sa
/// Valeur absolue est le signal de cadence (frame pacing) : une moyenne de 140 FPS peut etre
/// desagreable a jouer et cette grandeur est la seule a le dire.
/// </param>
/// <param name="PresentModes">
/// PM_METRIC_PRESENT_MODE : comment chaque image a atteint l'écran, valeurs de `PM_PRESENT_MODE`
/// (en-tête PresentMon 2.5.1 livré dans `third_party/presentmon`). « Composed » veut dire que le
/// compositeur de Windows recopie l'image ; « independent flip » qu'il s'efface. C'est ce qui dit, en
/// fenêtré sans bordure, si G-Sync et la latence se comportent comme en plein écran. Vide quand le
/// service ne donne pas la taille attendue.
/// </param>
public sealed record FrameCapture(
    IReadOnlyList<double> FrameTimesMs,
    IReadOnlyList<double> RenderPresentLatencyMs,
    IReadOnlyList<double> GpuBusyMs,
    IReadOnlyList<double>? GpuWaitMs = null,
    IReadOnlyList<double>? CpuBusyMs = null,
    IReadOnlyList<double>? InPresentApiMs = null,
    IReadOnlyList<double>? AnimationErrorMs = null,
    IReadOnlyList<int>? PresentModes = null,
    // B4 (10/10/2026) : DisplayLatency de PresentMon, « du début de l'image jusqu'à son affichage à l'écran », pour toute
    // application sans instrumentation. null = non demandée ou non livrée (taille inconnue).
    IReadOnlyList<double>? DisplayLatencyMs = null)
{
    /// <summary>Part des images passées par le compositeur, parmi celles dont le mode est connu ;
    /// null quand aucune ne l'est. Composed = valeurs 4, 5 et 6 de `PM_PRESENT_MODE`.</summary>
    public double? PartComposee
    {
        get
        {
            var connues = PresentModes?.Count(m => m != 0) ?? 0;
            return connues == 0 ? null : (double)PresentModes!.Count(m => m is 4 or 5 or 6) / connues;
        }
    }
}
