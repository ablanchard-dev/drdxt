using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Navigation;
using DrDxt.Core;
using DrDxt.Measurement;

namespace DrDxt.App.ViewModels;

// -----------------------------------------------------------------------
// M2 G1 — DashboardViewModel (MVVM, testable, no WPF dependency).
//
// Consumes an ApplyReport (+ its AbVerdict, master §10) and exposes — via
// [ObservableProperty] — everything a before/after dashboard binds: before/after
// frametime series, 1% & 0.1% lows, average FPS, the ESTIMATED PC latency
// (labelled "estimated"/"estimée" — honesty §4 P1) + margin, a headline key, the
// list of applied-and-kept vs auto-reverted tweak ids, and ShowGainBadge.
//
// HONESTY (master §5): a gain badge shows only for a measured gain. "No measurable
// gain" is a valid, assumed state (badge off). A measured regression that was
// auto-reverted is its own honest headline. A null verdict (the engine kept the
// batch but could not re-measure) is reported as "no measurable gain", never an
// invented gain. Latency is always labelled estimated — never sold as exact.
//
// ZERO hardcoded UI strings: every label/headline is a resx key the View resolves.
// -----------------------------------------------------------------------
public sealed partial class DashboardViewModel : ObservableObject
{
    // Headline keys (resx) for the three honest end states.
    private const string HeadlineMeasurableGain     = "dashboard.measurable_gain";
    private const string HeadlineNoMeasurableGain   = "dashboard.no_measurable_gain";
    private const string HeadlineRegressionReverted = "dashboard.regression_reverted";

    // Revert path (resx).
    private const string KeyRevertFailed = "dashboard.revert.failed";

    // Optional seams for the "Revert all" / "back to scan" path. Null in the read-only
    // preview/QA constructor (revert disabled); injected by DI for the live flow.
    private readonly ISafetyManager? _safety;
    private readonly INavigationService? _navigation;
    private readonly Func<object>? _scanFactory;
    private readonly Func<string, string> _localize;

    // The applied-and-kept snapshots, kept so RevertCommand can restore each original.
    private readonly List<TweakSnapshot> _keptSnapshots = new();

    /// <summary>Parameterless constructor for the QA preview + the pure VM tests:
    /// Load(report) fills it; the revert/back commands are inert (no seams).</summary>
    public DashboardViewModel()
        : this(safety: null, navigation: null, scanFactory: null, localize: static k => k)
    {
    }

    /// <summary>Live constructor: the revert path reverts each kept snapshot through
    /// <paramref name="safety"/> and "back" routes through <paramref name="navigation"/>.</summary>
    public DashboardViewModel(
        ISafetyManager? safety,
        INavigationService? navigation,
        Func<object>? scanFactory,
        Func<string, string> localize)
    {
        _safety = safety;
        _navigation = navigation;
        _scanFactory = scanFactory;
        _localize = localize;
    }

    /// <summary>14/09/2026 (C1 bis) — le pied de page portait « Version 1.0.0 » ecrit dans les .resx : une
    /// 3e source de version, fausse (l'installeur dit 0.9.0). Il lit la version de l'assembly, qui vient
    /// de Directory.Build.props comme celle de l'installeur.</summary>
    public string LabelFooterVersion => FooterVersion(_localize);

    internal static string FooterVersion(Func<string, string> localize) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, localize("UI.Footer.Version"),
        typeof(DashboardViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    // The verdict SummaryKey the engine emits for an auto-reverted regression.
    private const string VerdictRegressionReverted = "verdict.regression_reverted";

    // Les deux phrases de la note de resolution (resx). Cle + arguments, jamais une phrase
    // assemblee ici : ce depot a deja paye une fois un motif construit dans le moteur, qui
    // sortait en anglais au milieu d'un rapport francais.
    private const string KeyResolutionUnresolved = "dashboard.resolution.unresolved";
    private const string KeyResolutionUnknown = "dashboard.resolution.unknown";

    // Badge text keys (resx) — paired with the headline so the badge carries icon + TEXT,
    // never colour alone (a11y).
    private const string BadgeGain     = "dashboard.badge.gain";
    private const string BadgeNoGain   = "dashboard.badge.no_gain";
    private const string BadgeReverted = "dashboard.badge.reverted";

    // --- Headline / badge ------------------------------------------------

    [ObservableProperty]
    private string _headlineKey = HeadlineNoMeasurableGain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteDuGain))]
    private bool _showGainBadge;

    /// <summary>29/09/2026, quatrième critique : un gain annoncé au-dessus d'un 1 % low à la marge inconnue. Le gain
    /// vient alors du FPS moyen (AbComparer juge chaque chiffre avec sa marge) ; l'écran dit que le 1 % low ne
    /// conclut pas.</summary>
    public string? NoteDuGain => ShowGainBadge && MarginOfErrorLow is double.PositiveInfinity or double.NaN
        ? _localize("Dashboard.Gain.LowNonConclu")
        : null;

    /// <summary>The verdict SummaryKey carried by the report (resx), or null when the
    /// engine could not produce an A/B verdict (degraded re-bench).</summary>
    [ObservableProperty]
    private string? _verdictKey;

    /// <summary>
    /// L'ecart mesure et ce que la session pouvait resoudre — la phrase qui manquait.
    ///
    /// « Pas de gain mesurable » est un mensonge par omission : il laisse croire que le
    /// changement n'a rien fait, alors que le plus souvent **l'ecart existe et cette session ne
    /// pouvait pas le resoudre**. Cas mesure le 07/09/2026 sur la RTX 4060 : l'offset `+100/100`
    /// rend +2,7 % de calcul GPU reproductible, et le rejeu in-game ne resout que +/-4,8 %.
    ///
    /// `AbComparer.Compare` calculait deja cette resolution, dans une variable locale qui mourait
    /// avec la methode. Elle arrive enfin a l'ecran.
    ///
    /// <c>null</c> = rien a ajouter : soit le gain est prouve (la conclusion se suffit), soit
    /// aucun verdict n'a pu etre produit (aucun chiffre n'aurait de sens).
    /// </summary>
    [ObservableProperty]
    private string? _resolutionNote;

    // --- M8: bottleneck caveat -------------------------------------------
    //
    // Un reglage GPU ne peut pas produire de FPS si la session etait limitee par le CPU.
    // Le signal existait depuis M8 (BenchmarkResult.Bottleneck) mais n'etait dit nulle part :
    // il traversait le rapport en silence. Deux honnetetes en une :
    //   * sans gain -> l'utilisateur sait pourquoi, au lieu de conclure que le produit ne sert
    //     a rien ;
    //   * avec gain -> on ne laisse pas attribuer au reglage GPU un gain obtenu sur une session
    //     ou le GPU n'etait pas le limiteur (anti-sur-attribution).
    // On ne parle que sur un CPU/other-bound mesure : la bande indeterminee (75-90 %) et un
    // GPU-busy jamais lu ne disent rien -- UNKNOWN vaut mieux qu'invente.

    /// <summary>Vrai quand la session mesuree etait limitee par le CPU (ou autre chose que le
    /// GPU) : le tableau de bord doit alors le dire.</summary>
    [ObservableProperty]
    private bool _showBottleneckCaveat;

    /// <summary>Cle resx du caveat affiche quand <see cref="ShowBottleneckCaveat"/> est vrai.</summary>
    public string BottleneckCaveatKey => CaveatCpuBound;

    private const string CaveatCpuBound = "dashboard.caveat.cpu_bound";

    // --- Metric label keys (resx — never hardcoded text) -----------------

    /// <summary>Latency is always labelled "estimated" (honesty §4 P1).</summary>
    public string LatencyLabelKey => "metric.pc_latency_estimated";
    /// <summary>GPU-busy % of frame time — the GPU-bound (≈100) vs CPU-bound (≪) diagnostic.</summary>
    public string GpuBusyLabelKey => "metric.gpu_busy";
    public string AvgFpsLabelKey => "metric.avg_fps";
    public string P1LowLabelKey => "metric.p1_low";
    public string P01LowLabelKey => "metric.p01_low";
    public string RevertAllActionKey => "action.revert_all";
    public string BackToScanActionKey => "action.back_to_scan";

    // --- View chrome label keys (resx — additive, consumed only by the View) ---
    // These are static keys the DashboardView binds for headers/axes/sections. They
    // keep the View free of hardcoded strings without reshaping the VM's data API.
    public string SubtitleKey => "dashboard.subtitle";
    public string FrametimeChartTitleKey => "dashboard.chart.frametime.title";
    public string FrametimeChartCaptionKey => "dashboard.chart.frametime.caption";
    public string SeriesBeforeKey => "dashboard.series.before";
    public string SeriesAfterKey => "dashboard.series.after";
    public string LowsTitleKey => "dashboard.lows.title";
    public string LatencyTitleKey => "dashboard.latency.title";
    public string ChangesTitleKey => "dashboard.changes.title";
    public string ChangesAppliedTitleKey => "dashboard.changes.applied";
    public string ChangesRevertedTitleKey => "dashboard.changes.reverted";
    public string ChangesNoneKey => "dashboard.changes.none";

    /// <summary>Badge text key, driven by the same honest state as <see cref="ShowGainBadge"/>:
    /// gain / no measurable gain / auto-reverted. Always rendered with an icon AND this text
    /// (never colour alone) for accessibility.</summary>
    [ObservableProperty]
    private string _badgeTextKey = "dashboard.badge.no_gain";

    // --- Frametime series (before in muted, after in accent — View concern) ---

    public ObservableCollection<double> BeforeFrameTimes { get; } = new();
    public ObservableCollection<double> AfterFrameTimes { get; } = new();

    // --- Aggregate metrics, before / after -------------------------------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureFps))] private double _avgFpsBefore;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureFps))] private double _avgFpsAfter;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureLow))] private double _p1LowBefore;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureLow))] private double _p1LowAfter;
    [ObservableProperty] private double _p01LowBefore;
    [ObservableProperty] private double _p01LowAfter;

    /// <summary>Estimated PC latency (ms) before applying. Null when PresentMon did
    /// not yield a latency estimate. Always shown with the "estimated" label.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureLatence))] private double? _estimatedPcLatencyBeforeMs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LectureLatence))] private double? _estimatedPcLatencyAfterMs;

    /// <summary>GPU-busy as a % of frame time, before / after. Null when GPU busy was not
    /// measured. A real measured ratio (no invented threshold) — the GPU- vs CPU-bound signal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuBusyMesure), nameof(GpuNonMesureTexte), nameof(LectureGpu))]
    private double? _gpuBusyPercentBefore;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuBusyMesure), nameof(GpuNonMesureTexte), nameof(LectureGpu))]
    private double? _gpuBusyPercentAfter;

    /// <summary>28/09/2026 : sans aucune des deux mesures, la case affichait deux tirets et un « % », un tiret et une
    /// unité qui ressemblaient à une valeur. Elle dit maintenant « non mesuré ».</summary>
    public bool GpuBusyMesure => GpuBusyPercentBefore is not null || GpuBusyPercentAfter is not null;
    public string? GpuNonMesureTexte => GpuBusyMesure ? null : _localize("Dashboard.NonMesure");

    // 29/09/2026 : ce qu'un lecteur d'écran lit pour chaque case (nom du chiffre, avant, après) ; il ne
    // lisait qu'une suite de nombres et une flèche.
    public string LectureFps => Lecture(AvgFpsLabelKey, AvgFpsBefore, AvgFpsAfter);
    public string LectureLow => Lecture(P1LowLabelKey, P1LowBefore, P1LowAfter);
    public string LectureLatence => Lecture(LatencyLabelKey, EstimatedPcLatencyBeforeMs, EstimatedPcLatencyAfterMs);
    public string LectureGpu => GpuBusyMesure
        ? Lecture(GpuBusyLabelKey, GpuBusyPercentBefore, GpuBusyPercentAfter)
        : $"{_localize(GpuBusyLabelKey)} : {GpuNonMesureTexte}";

    private string Lecture(string cle, double? avant, double? apres)
    {
        string Valeur(double? v) => v is { } x ? x.ToString("0.#", CultureInfo.CurrentCulture) : _localize("Dashboard.NonMesure");
        return string.Format(CultureInfo.CurrentCulture, _localize("Dashboard.Lecture"), _localize(cle), Valeur(avant), Valeur(apres));
    }

    /// <summary>The combined margin of error to render alongside the metrics so the
    /// user always sees the uncertainty (master §4: never hide the margin).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MargeFpsTexte))]
    private double _marginOfError;

    /// <summary>Bornes de l'axe du graphique des temps par image (haut = la trame la plus lente).</summary>
    [ObservableProperty] private string? _echelleHautTexte;
    [ObservableProperty] private string? _echelleBasTexte;

    /// <summary>29/09/2026 : l'équivalent texte du graphique pour un lecteur d'écran, la médiane des temps
    /// par image avant puis après.</summary>
    [ObservableProperty] private string? _lectureGraphique;

    private static double Mediane(IReadOnlyList<double> v)
    {
        var tri = v.OrderBy(x => x).ToList();
        return tri[tri.Count / 2];
    }

    /// <summary>
    /// La marge du FPS moyen, telle que l'écran la montre (23/09/2026 : elle était calculée et
    /// affichée nulle part). +∞ sous trois passes : « marge inconnue », jamais « ± 0 ».
    /// </summary>
    public string? MargeFpsTexte => MarginOfError switch
    {
        double.PositiveInfinity or double.NaN => _localize("Dashboard.Marge.Inconnue"),
        > 0 => Format(_localize("Dashboard.Marge.Fps"), Math.Round(MarginOfError, 1)),
        _ => null,
    };

    /// <summary>27/09/2026 : la marge du 1 % low, qui a la sienne depuis le 26/09 (`P1LowMarginOfError`), plus
    /// large que celle de la moyenne. L'écran montrait « avant → après » des lows sans elle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MargeLowTexte), nameof(NoteDuGain))]
    private double _marginOfErrorLow;

    public string? MargeLowTexte => MarginOfErrorLow switch
    {
        double.PositiveInfinity or double.NaN => _localize("Dashboard.Marge.Inconnue"),
        > 0 => Format(_localize("Dashboard.Marge.Fps"), Math.Round(MarginOfErrorLow, 1)),
        _ => null,
    };

    /// <summary>28/09/2026 : la marge du 0,1 % low, le chiffre le plus fragile de l'écran ; les barres le
    /// montraient sans elle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MargeLow01Texte))]
    private double _marginOfErrorLow01;

    public string? MargeLow01Texte => MarginOfErrorLow01 switch
    {
        double.PositiveInfinity or double.NaN => _localize("Dashboard.Marge.Inconnue"),
        > 0 => Format(_localize("Dashboard.Marge.Fps"), Math.Round(MarginOfErrorLow01, 1)),
        _ => null,
    };

    // --- Applied vs auto-reverted tweak ids ------------------------------

    /// <summary>Ids of tweaks that were applied AND kept (not auto-reverted).</summary>
    public ObservableCollection<string> AppliedTweakIds { get; } = new();

    /// <summary>Ids of tweaks that were auto-reverted (regression) or otherwise rolled
    /// back. The dashboard lists these so the change history is fully transparent.</summary>
    public ObservableCollection<string> RevertedTweakIds { get; } = new();

    // --- Rich change rows for the View (additive over the id lists above) -----

    /// <summary>Applied-and-kept tweaks as rich rows (title + summary keys) the
    /// dashboard renders. Mirrors <see cref="AppliedTweakIds"/> one-to-one.</summary>
    public ObservableCollection<TweakChangeItem> AppliedChanges { get; } = new();

    /// <summary>Auto-reverted tweaks as rich rows. Mirrors <see cref="RevertedTweakIds"/>.</summary>
    public ObservableCollection<TweakChangeItem> RevertedChanges { get; } = new();

    /// <summary>Loads an apply report into the dashboard. Idempotent: clears prior
    /// state first, so the same VM can be reused for a second apply.</summary>
    public void Load(ApplyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        BeforeFrameTimes.Clear();
        AfterFrameTimes.Clear();
        AppliedTweakIds.Clear();
        RevertedTweakIds.Clear();
        AppliedChanges.Clear();
        RevertedChanges.Clear();
        _keptSnapshots.Clear();
        HasReverted = false;
        RevertErrorMessage = null;

        PopulateChanges(report.Outcomes);
        PopulateVerdict(report.Verdict);
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void PopulateChanges(IReadOnlyList<ApplyOutcome> outcomes)
    {
        foreach (var o in outcomes)
        {
            if (o.AutoReverted)
            {
                RevertedTweakIds.Add(o.TweakId);
                RevertedChanges.Add(Row(o.TweakId, wasReverted: true));
            }
            else if (o.Applied)
            {
                AppliedTweakIds.Add(o.TweakId);
                AppliedChanges.Add(Row(o.TweakId, wasReverted: false));
                // Keep the snapshot (real captured-original payload) for RevertCommand.
                if (o.Snapshot is { } snap)
                    _keptSnapshots.Add(snap);
            }
        }
    }

    // --- Revert all / back-to-scan ---------------------------------------

    /// <summary>True after the user reverted the kept changes (drives the UI state).</summary>
    [ObservableProperty]
    private bool _hasReverted;

    /// <summary>Set when one or more reverts failed — surfaced honestly, never swallowed.</summary>
    [ObservableProperty]
    private string? _revertErrorMessage;

    /// <summary>Revert is offered only when there is at least one applied-and-kept change
    /// AND the live revert seam is wired (the QA preview constructor leaves it inert).</summary>
    public bool CanRevert => _safety is not null && _keptSnapshots.Count > 0;

    /// <summary>Ce que l'infobulle du bouton dit, selon qu'il peut agir ou non.
    /// <para>
    /// 17/09/2026 — le bouton reste VISIBLE quand il est inerte, et rien n'expliquait
    /// pourquoi : la personne voit le filet de securite, clique, et il ne se passe rien.
    /// Un bouton grise muet est un cul-de-sac, surtout celui-la : c'est celui qu'on
    /// cherche quand un reglage a fait empirer les choses.
    /// </para></summary>
    public string RevertHintKey =>
        CanRevert ? "dashboard.revert.available" : "dashboard.revert.unavailable";

    /// <summary>04/10/2026 (plan 6.1) : ce qui suit l'annulation réussie d'un levier (manifeste du service, journal des
    /// leviers). L'appli le pose ; <c>null</c> = rien (les tests qui ne le vérifient pas).</summary>
    internal Func<string, Task<Result>>? ApresAnnulation { get; set; }

    /// <summary>Reverts every applied-and-kept snapshot through the SafetyManager (the
    /// standalone snapshot-revert path). Each id that reverts cleanly moves from the
    /// applied list to the reverted list; any that fail stay applied and raise a message.
    /// Already auto-reverted tweaks are not touched (they were never in _keptSnapshots).</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (_safety is null)
            return;

        RevertErrorMessage = null;
        var failures = new List<string>();
        var restesAuManifeste = new List<string>();

        // Iterate a copy: a clean revert mutates the lists.
        foreach (var snapshot in _keptSnapshots.ToList())
        {
            Result result;
            try
            {
                result = await _safety.RevertAsync(snapshot).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message);
            }

            if (result.IsOk)
            {
                MoveToReverted(snapshot.TweakId);
                if (ApresAnnulation is { } apres
                    && await apres(snapshot.TweakId).ConfigureAwait(true) is { IsOk: false } reste)
                    restesAuManifeste.Add(string.Format(CultureInfo.CurrentCulture,
                        _localize("Changement.PasSortiDuManifeste"), _localize(TweakDisplayKeys.TitleKey(snapshot.TweakId)),
                        reste.Error?.Message));
            }
            else
                failures.Add(snapshot.TweakId);
        }

        if (failures.Count > 0)
            RevertErrorMessage = _localize(KeyRevertFailed);
        if (restesAuManifeste.Count > 0)
            RevertErrorMessage = string.Join(" ", new[] { RevertErrorMessage }.Concat(restesAuManifeste).Where(m => m is { Length: > 0 }));

        if (RevertedTweakIds.Count > 0)
            HasReverted = true;

        RevertCommand.NotifyCanExecuteChanged();
    }

    private void MoveToReverted(string tweakId)
    {
        _keptSnapshots.RemoveAll(s => string.Equals(s.TweakId, tweakId, StringComparison.Ordinal));

        AppliedTweakIds.Remove(tweakId);
        var appliedRow = AppliedChanges.FirstOrDefault(r => r.TweakId == tweakId);
        if (appliedRow is not null)
            AppliedChanges.Remove(appliedRow);

        if (!RevertedTweakIds.Contains(tweakId))
            RevertedTweakIds.Add(tweakId);
        if (RevertedChanges.All(r => r.TweakId != tweakId))
            RevertedChanges.Add(Row(tweakId, wasReverted: true));
    }

    /// <summary>Navigate back to the scan view (the funnel entry point).</summary>
    [RelayCommand]
    private void BackToScan()
    {
        if (_navigation is null || _scanFactory is null)
            return;
        _navigation.NavigateToScan(_scanFactory());
    }

    private static TweakChangeItem Row(string tweakId, bool wasReverted) =>
        new(tweakId, TweakDisplayKeys.TitleKey(tweakId), TweakDisplayKeys.SummaryKey(tweakId), wasReverted);

    /// <summary>La phrase qui dit l'ecart et la resolution — ou <c>null</c> quand il n'y a rien a
    /// ajouter. Voir <see cref="ResolutionNote"/> pour le pourquoi.</summary>
    private string? NoteDeResolution(AbVerdict? verdict)
    {
        // Aucun verdict : la comparaison n'a pas tenu (re-mesure impossible, ou deux passes qui
        // n'ont pas mesure la meme session). Afficher un chiffre serait pire que se taire.
        if (verdict is null) return null;

        var preuve = AbComparer.Evidence(verdict.Before, verdict.After).Game?.Preuve;
        if (preuve is not { } p) return null;

        return p.Outcome switch
        {
            // Un gain prouve se suffit : la note encombrerait la conclusion.
            EvidenceOutcome.ProvenGain => null,
            EvidenceOutcome.Invalid => null,

            // La resolution vaut +∞ sous trois passes. Afficher « ±∞ % » serait grotesque, et
            // se taire ferait croire a une mesure fine : il faut une troisieme phrase.
            _ when !double.IsFinite(p.ResolutionPct) =>
                Format(_localize(KeyResolutionUnknown), p.DeltaPct),

            _ => Format(_localize(KeyResolutionUnresolved), p.DeltaPct, p.ResolutionPct),
        };
    }

    private static string Format(string modele, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, modele, args);

    private void PopulateVerdict(AbVerdict? verdict)
    {
        VerdictKey = verdict?.SummaryKey;
        ResolutionNote = NoteDeResolution(verdict);

        if (verdict is null)
        {
            // Degraded: the batch was kept but no A/B verdict could be produced. Never
            // invent a gain — the honest headline is "no measurable gain", no badge.
            HeadlineKey = HeadlineNoMeasurableGain;
            ShowGainBadge = false;
            BadgeTextKey = BadgeNoGain;
            ShowBottleneckCaveat = false; // aucun verdict => aucun limiteur a nommer
            ResetMetrics();
            return;
        }

        // M8 — le limiteur de la session NON MODIFIEE (AbVerdict.Bottleneck => Before). On ne
        // parle que sur un CPU/other-bound mesure ; Indeterminate et GPU-busy non lu se taisent.
        ShowBottleneckCaveat = verdict.Bottleneck == Bottleneck.CpuOrOtherBound;

        BindMetrics(verdict.Before, verdict.After);

        if (string.Equals(verdict.SummaryKey, VerdictRegressionReverted, StringComparison.Ordinal))
        {
            HeadlineKey = HeadlineRegressionReverted;
            ShowGainBadge = false;
            BadgeTextKey = BadgeReverted;
        }
        else if (verdict.IsMeasurableGain)
        {
            HeadlineKey = HeadlineMeasurableGain;
            ShowGainBadge = true;
            BadgeTextKey = BadgeGain;
        }
        else
        {
            HeadlineKey = HeadlineNoMeasurableGain;
            ShowGainBadge = false;
            BadgeTextKey = BadgeNoGain;
        }
    }

    private void BindMetrics(BenchmarkResult before, BenchmarkResult after)
    {
        foreach (var ms in before.FrameTimesMs)
            BeforeFrameTimes.Add(ms);
        foreach (var ms in after.FrameTimesMs)
            AfterFrameTimes.Add(ms);

        // L'échelle du graphique : la même que FrameSeriesToPointsConverter (min en bas, max en haut,
        // sur les deux séries). 23/09/2026 : l'axe n'avait aucune valeur.
        var toutes = before.FrameTimesMs.Concat(after.FrameTimesMs).ToList();
        EchelleHautTexte = toutes.Count > 0 ? Format("{0:0.#} ms", toutes.Max()) : null;
        EchelleBasTexte = toutes.Count > 0 ? Format("{0:0.#} ms", toutes.Min()) : null;
        LectureGraphique = before.FrameTimesMs.Count > 0 && after.FrameTimesMs.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, _localize("Dashboard.Lecture.Graphique"),
                Mediane(before.FrameTimesMs), Mediane(after.FrameTimesMs))
            : null;

        AvgFpsBefore = before.AvgFps;
        AvgFpsAfter = after.AvgFps;
        P1LowBefore = before.P1LowFps;
        P1LowAfter = after.P1LowFps;
        P01LowBefore = before.P01LowFps;
        P01LowAfter = after.P01LowFps;
        EstimatedPcLatencyBeforeMs = before.EstimatedPcLatencyMs;
        EstimatedPcLatencyAfterMs = after.EstimatedPcLatencyMs;
        GpuBusyPercentBefore = before.GpuBusyPercent;
        GpuBusyPercentAfter = after.GpuBusyPercent;
        // Combined uncertainty: the "after" margin is what bounds the new reading.
        MarginOfError = after.MarginOfError;
        MarginOfErrorLow = after.P1LowMarginOfError;
        MarginOfErrorLow01 = after.P01LowMarginOfError;
    }

    private void ResetMetrics()
    {
        AvgFpsBefore = AvgFpsAfter = 0;
        P1LowBefore = P1LowAfter = 0;
        P01LowBefore = P01LowAfter = 0;
        EstimatedPcLatencyBeforeMs = EstimatedPcLatencyAfterMs = null;
        GpuBusyPercentBefore = GpuBusyPercentAfter = null;
        MarginOfError = 0;
        MarginOfErrorLow = 0;
        MarginOfErrorLow01 = 0;
        EchelleHautTexte = EchelleBasTexte = LectureGraphique = null;
    }
}
