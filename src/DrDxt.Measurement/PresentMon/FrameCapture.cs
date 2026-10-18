namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Les séries par image d'une capture. <see cref="FrameTimesMs"/> donne les FPS et les lows ;
/// <see cref="RenderPresentLatencyMs"/> n'est que la part présentation→affichage (render-present)
/// du pipeline, pas la latence système / clic-à-photon (qui demande aussi la part entrée via les
/// marqueurs Reflex et le balayage de l'écran). <see cref="GpuBusyMs"/> est le temps de travail
/// réel du GPU sur chaque image (≤ le temps d'image) ; GpuBusy/FrameTime révèle si une image est
/// limitée par le GPU (≈1) ou par le CPU (≪1). Tout est informatif, jamais un score ni un
/// déclencheur de retour automatique. Une série est vide quand sa grandeur est indisponible. Les
/// séries sont parallèles : une entrée par image capturée.
///
/// Les quatre series suivantes servent le classifieur de goulot (note de conception, voir
/// `BottleneckClassifier`), qui demande 8 grandeurs : sans elles, aucun etat au-dela de
/// « GPU-bound / pas GPU-bound » ne serait calculable. Optionnels et en
/// queue : toutes les constructions existantes restent valides, et une serie vide veut dire
/// « pas mesuree », jamais « zero ».
/// </summary>
/// <param name="GpuWaitMs">
/// PM_METRIC_GPU_WAIT : le temps ou le GPU est inactif alors qu'il travaille sur la frame. Une
/// attente elevee dit que quelque chose d'autre (l'app, le CPU) donne le tempo. C'est la grandeur
/// qui separe « CPU-bound » de « simplement pas GPU-bound ».
/// </param>
/// <param name="CpuBusyMs">
/// PM_METRIC_CPU_BUSY : le travail CPU de l'application sur la frame. Ce n'est pas la charge
/// CPU de la machine : la doc PresentMon le dit explicitement, et confondre les deux ferait
/// accuser le CPU d'un systeme charge par autre chose.
/// </param>
/// <param name="InPresentApiMs">
/// PM_METRIC_IN_PRESENT_API : le temps passe dans l'appel Present(). C'est la signature d'un
/// blocage vsync / cap / compositeur : la frame est prete et on attend.
/// </param>
/// <param name="AnimationErrorMs">
/// PM_METRIC_ANIMATION_ERROR : l'ecart entre le temps simule et le temps affiche. Le p95 de sa
/// valeur absolue est le signal de cadence (frame pacing) : une moyenne de 140 FPS peut etre
/// desagreable a jouer et cette grandeur est la seule a le dire.
/// </param>
/// <param name="PresentModes">
/// PM_METRIC_PRESENT_MODE : comment chaque image a atteint l'écran, valeurs de `PM_PRESENT_MODE`
/// (en-tête PresentMon 2.5.1 livré dans `third_party/presentmon`). « Composed » veut dire que le
/// compositeur de Windows recopie l'image ; « independent flip » qu'il s'efface. C'est ce qui dit, en
/// fenêtré sans bordure, si G-Sync et la latence se comportent comme en plein écran. Vide quand le
/// service ne donne pas la taille attendue. Lu par <c>FrametimeStatistics.PartComposee</c> pour la frise des parties.
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
    // DisplayLatency de PresentMon, « du début de l'image jusqu'à son affichage à l'écran », pour toute
    // application sans instrumentation. null = non demandée ou non livrée (taille inconnue).
    IReadOnlyList<double>? DisplayLatencyMs = null,
    // PcLatency de PresentMon, de l'entrée du joueur jusqu'à l'écran ; NaN par image quand le jeu ne pose pas les
    // marqueurs de latence. null = non livrée.
    IReadOnlyList<double>? PcLatencyMs = null);
