using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>L'identite de ce qui a ete optimise : sans elle, un profil s'appliquerait a une autre machine, un autre
/// pilote ou une autre resolution, avec un gain que plus rien ne soutient.</summary>
/// <param name="ProcessName">Le jeu. C'est lui qu'on optimise, pas « le PC ».</param>
/// <param name="GpuName">Un GPU remplace invalide le profil.</param>
/// <param name="GpuDriver">Un nouveau pilote NVIDIA : le profil n'est plus automatiquement prouve.</param>
/// <param name="WindowsVersion">Une mise a jour de fonctionnalite change l'ordonnanceur.</param>
/// <param name="DisplayMode">Resolution et rafraichissement : les changer demande une revalidation.</param>
public sealed record GameProfileIdentity(
    string ProcessName,
    string? GpuName,
    string? GpuDriver,
    string? WindowsVersion,
    string? DisplayMode);

/// <summary>Le livrable : ce que DrDXT a essaye sur ce jeu, et ce qu'il a pu prouver.</summary>
public sealed record GameOptimizationProfile(
    GameProfileIdentity Identity,
    OptimizationGoal Goal,
    GameMetrics? Baseline,
    SceneResolution Scene,
    IReadOnlyList<CandidateResult> Results,
    bool EndedInBestState,
    string SceneExplanation,
    /// <summary>Ce que le focus a observe pour chaque passe, dans l'ordre. <c>null</c> = pas
    /// observe du tout, a ne jamais confondre avec « observe a 0 % ».</summary>
    IReadOnlyList<double?>? FocusParPasse = null,

    /// <summary>Ce que l'A/A resout sur la moyenne seule. <c>null</c> = pas assez de passes.</summary>
    SceneResolution? ResolutionMoyenne = null,

    /// <summary>Ce que l'A/A resout sur le 1 % low seul. <c>null</c> = pas assez de passes. Separee de la moyenne,
    /// elle dit laquelle ruine la resolution : mesure en session Warzone le 07/09/2026, score composite a +/-74 %, ou un
    /// candidat qui n'ecrit rien ne se distingue pas d'un vrai gain.</summary>
    SceneResolution? ResolutionUnPourcentLow = null,

    /// <summary>Ce que l'A/A resout sur le p95 frametime seul : la grandeur de queue qui sait
    /// decider. <c>null</c> = pas assez de passes, ou p95 non mesure.</summary>
    SceneResolution? ResolutionP95Frametime = null,

    /// <summary>Ce que le materiel a fait pendant chaque passe, dans l'ordre. <c>null</c> = aucune
    /// sonde branchee. Une observation, jamais une cause.</summary>
    IReadOnlyList<HardwareObservation>? MaterielParPasse = null,

    /// <summary>L'occupation GPU mediane sur les passes de la session, en pourcent. Elle vient des
    /// fenetres reellement utilisees pour decider, pas d'une capture prise avant.</summary>
    double? GpuBusyMedianPct = null,

    /// <summary>Ou la mesure a ete prise, et ce que ca autorise a en conclure. Sans lui, un profil
    /// ne au fond d'un lobby et un profil ne en pleine partie se ressemblent trait pour trait.</summary>
    SceneContract? Scene2 = null,

    /// <summary>Ce que le materiel a montre pendant les fenetres mesurees. Une observation,
    /// jamais une cause : nommer un plafond n'explique pas un ecart de FPS.</summary>
    GpuLimiterObserved Bridage = GpuLimiterObserved.Inconnu,

    /// <summary>La capture de reference, telle quelle : les ratios du classifieur de goulot (GPU busy, attente GPU,
    /// travail CPU de l'app, latence, cadence), sans quoi une machine GPU-bound a 97 % et une que le GPU attend
    /// recevraient les memes leviers. L'appelant classe : le classifieur vit dans une couche que la session ne connait pas.</summary>
    BenchmarkResult? MesureDeReference = null)
{
    public IEnumerable<CandidateResult> Kept => Results.Where(r => r.Outcome == CandidateOutcome.Kept);
    public IEnumerable<CandidateResult> Reverted => Results.Where(r =>
        r.Outcome is CandidateOutcome.RejectedRegression or CandidateOutcome.RejectedUnresolved
                  or CandidateOutcome.Unstable or CandidateOutcome.ApplyFailed);
    public IEnumerable<CandidateResult> NotTestable => Results.Where(r => r.Outcome == CandidateOutcome.NotAttempted);
}

/// <summary>
/// « Optimiser ce jeu » : essaie des reglages pendant une vraie partie, en alternance, et ne garde
/// que ceux dont le gain se mesure au-dessus du bruit.
///
/// Il branche le moteur sans le refaire : `AutopilotEngine` reste pur et ignorant du jeu ; la session lui apporte une
/// facon de mesurer une partie (<see cref="IBenchmarkRunner"/>, qui repare son propre service de mesure) et une
/// identite pour le profil.
///
/// Un score melange (`0,35 x moyenne + 0,65 x 1 % low`) ne se resout pas : sur une session Warzone reelle, **+/-72 %**
/// contre **+/-4,6 %** pour la moyenne seule. Chaque grandeur est donc jugee separement (<see cref="GameMetricSet"/>),
/// l'arbitrage vient apres (<see cref="KeepPolicy.DecideParMetrique"/>).
///
/// « Aucun reglage conserve : cette partie etait trop bruitee » est une reponse honnete, preferable a un faux +3 %.
/// </summary>
public sealed class GameOptimizationSession
{
    private readonly IBenchmarkRunner _bench;
    private readonly Func<GameProfileIdentity> _identite;

    /// <summary>
    /// Duree d'une passe : **10 s, mesuree**. A temps de jeu egal, des passes courtes resolvent mieux : la marge vaut
    /// `t95(n-2) x sigma / racine(n)`, et passer de 3 a 17 passes fait chuter t95 de 4,30 a 2,13 et multiplie racine(n)
    /// par 2,4 (~4,8, que la hausse de sigma ne compense pas).
    /// Huit captures Warzone mesurees le 01/09 et rejouees par ce code, resolution mediane : 10 s → ~3,4 %, 20 s → ~4,8 %,
    /// 45 s → ~30 % ; sept sur huit sont meilleures a 10 s.
    /// A 10 s le 1 % low ne repose que sur ~14 frames, mais a 240 s de jeu `tools/tailbench` (8 captures reelles) mesure
    ///   protocole                 moyenne   p95 frametime   1 % low
    ///   10 s x 24 alternances      2,7 %        3,3 %         8,7 %     ← meilleur partout
    ///   15 s x 16 alternances      3,4 %        3,2 %        10,1 %
    ///   20 s x 12 alternances      5,5 %        7,2 %        19,6 %
    ///   30 s x 8  alternances      3,9 %        4,6 %        18,2 %
    /// Le nombre de points bat la precision de chaque point. Details : `docs/PROTOCOLES-DE-MESURE-IN-GAME.md`.
    /// </summary>
    public static readonly TimeSpan DureeParPasse = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Passes d'alternance : **24, soit 4 minutes de jeu**, apres les 3 passes d'A/A, plus jusqu'a 3 passes refaites
    /// apres un alt-tab : 5 minutes au pire pour un levier. A 4 passes le modele n'a qu'un degre de liberte (t95 =
    /// 12,706) : rien sous ~14 %. Mesure du 07/09/2026, bruit vrai sur la moyenne : 13,7 % a 4, 5,2 % a 8, 3,9 % a 12,
    /// 2,7 % a 24.
    /// Duree fixe, pas un plafond : s'arreter sur un franchissement serait de l'optional stopping (voir AutopilotEngine).
    /// Un levier qui declare son effet attendu fixe ses alternances avant la mesure (Decidabilite.AlternancesPour : 12 ou 24).
    /// </summary>
    public const int AlternancesMax = 24;

    /// <summary>Une passe = un run du banc : chaque passe reste un point independant, sinon l'A/A mesurerait la
    /// dispersion interne d'une passe au lieu de celle de la scene.</summary>
    private const int RunsParPasse = 1;

    /// <summary>Le reservoir des candidats juges. null = rien n'est consigne (outils, tests).</summary>
    private readonly JournalDesCandidats? _journal;

    public GameOptimizationSession(IBenchmarkRunner bench, Func<GameProfileIdentity> identite,
        JournalDesCandidats? journal = null)
    {
        _bench = bench;
        _identite = identite;
        _journal = journal;
    }

    /// <summary>Optimise ce jeu.</summary>
    /// <param name="targetPid">Le processus du jeu. 0 = aucun jeu : la session le dit et s'arrete.</param>
    /// <param name="dureeParPasse">Duree d'une passe. <c>null</c> = <see cref="DureeParPasse"/>. Ce qui compte est le
    /// nombre de passes que le temps total permet : a temps egal, plus courtes resolvent mieux (07/09/2026, huit
    /// captures reelles).</param>
    public GameOptimizationProfile Run(
        int targetPid,
        IReadOnlyList<IAutopilotCandidate> candidats,
        OptimizationGoal but = OptimizationGoal.Competitive,
        int passesAa = 3,
        Func<bool>? stable = null,
        TimeSpan? dureeParPasse = null,
        int alternancesMax = AlternancesMax,
        SceneContract? scene = null,
        // Le joueur peut arreter : le moteur rend alors l'etat de reference et s'arrete.
        CancellationToken ct = default)
    {
        var duree = dureeParPasse ?? DureeParPasse;
        GameMetrics? baseline = null;
        BenchmarkResult? mesureDeReference = null;
        var passesHorsFocus = 0;
        var journalFocus = new List<double?>();
        var journalMetriques = new List<GameMetrics>();
        var journalMateriel = new List<HardwareObservation>();
        var journalGpuBusy = new List<double>();

        // Une passe perdue pour un alt-tab se refait : mesure le 07/09/2026 sur Warzone (focus 100 % / 62 % / 100 %), une seule
        // bascule laissait deux passes sur trois, donc aucune resolution. Borne au nombre de passes demandees : un
        // joueur toujours ailleurs recoit ce qu'on a, plutot qu'une mesure sans fin.
        var rattrapagesFocus = Math.Max(3, passesAa);
        var derniereEcarteePourFocus = false;

        GameMetrics? Mesurer()
        {
            for (;;)
            {
                var v = MesurerUneFois();
                if (v is not null || rattrapagesFocus <= 0 || !derniereEcarteePourFocus) return v;
                rattrapagesFocus--;
            }
        }

        GameMetrics? MesurerUneFois()
        {
            derniereEcarteePourFocus = false;
            var r = _bench.RunAsync(targetPid, duree, RunsParPasse, ct)
                .GetAwaiter().GetResult();

            if (!r.IsOk || r.Value is null || !(r.Value.AvgFps > 0))
                return null;   // passe inexploitable : elle ne compte pas, elle n'est pas « basse ».

            // Le focus observe decide : une passe derriere une autre fenetre ne compte pas, comme une passe sans frame ;
            // une passe entierement jouee compte, bonne ou catastrophique. Trace par passe : sinon une passe ecartee et
            // une passe jamais observee se ressemblent.
            journalFocus.Add(r.Value.ForegroundFraction);

            if (r.Value.ForegroundFraction is { } f && f < FocusObservation.FractionMinimale)
            {
                passesHorsFocus++;
                derniereEcarteePourFocus = true;
                return null;
            }

            // La telemetrie vient des memes fenetres que les frames : une capture prise avant
            // l'experience decrirait un autre moment du jeu que celui sur lequel l'Autopilot decide.
            if (r.Value.Materiel is { Echantillons: > 0 } mat) journalMateriel.Add(mat);
            if (r.Value.GpuBusyPercent is { } busy) journalGpuBusy.Add(busy);

            var m = new GameMetrics(
                r.Value.AvgFps, r.Value.P1LowFps, r.Value.P01LowFps, r.Value.P95FrametimeMs);
            baseline ??= m;   // la toute premiere passe exploitable est la reference affichee.
            mesureDeReference ??= r.Value;
            journalMetriques.Add(m);
            return m;
        }

        var profil = AutopilotEngine.Run(candidats, Mesurer, passesAa, stable, but, alternancesMax,
            scene?.Representativite ?? SceneRepresentativity.Unknown, ct);

        // Laquelle des grandeurs ruine la resolution : les passes d'A/A reprobees separement le disent (presque toujours
        // le 1 % low, un percentile sur ~14 frames a 140 i/s). Elles expliquent, elles ne decident rien.
        var aa = journalMetriques.Take(passesAa).ToList();
        var resMoyenne = aa.Count >= 3 ? SceneResolution.Probe(aa.Select(m => m.AvgFps).ToList()) : null;
        var resP1Low = aa.Count >= 3 ? SceneResolution.Probe(aa.Select(m => m.P1LowFps).ToList()) : null;
        var resP95 = aa.Count >= 3
            ? SceneResolution.Probe(aa.Select(m => GameMetricSet.Valeur(m, GameMetricKind.P95Frametime)).ToList())
            : null;

        var sortie = new GameOptimizationProfile(
            _identite(),
            but,
            baseline,
            profil.Scene,
            profil.Results,
            profil.EndedInBestState,
            Expliquer(profil.Scene, scene ?? SceneContract.Inconnue)
                + (passesHorsFocus > 0
                    ? Docteur.T(passesHorsFocus == 1 ? "GOS.HorsFocus.Une" : "GOS.HorsFocus.Plusieurs", passesHorsFocus)
                    : string.Empty),
            journalFocus,
            resMoyenne,
            resP1Low,
            resP95,
            journalMateriel.Count > 0 ? journalMateriel : null,
            journalGpuBusy.Count > 0 ? Mediane(journalGpuBusy) : null,
            scene ?? SceneContract.Inconnue,
            Brider(journalMateriel, journalGpuBusy),
            mesureDeReference);

        // Ne leve jamais : un journal qui ne s'ecrit pas ne coute pas la session au joueur.
        _journal?.Consigner(sortie);
        return sortie;
    }

    /// <summary>Ce que le materiel a montre sur l'ensemble des passes. Les raisons s'unissent
    /// (un plafond momentane a eu lieu), la frequence mediane se prend sur les medianes de passe,
    /// et le minimum est le plus bas vu n'importe ou.</summary>
    private static GpuLimiterObserved Brider(
        List<HardwareObservation> materiel, List<double> gpuBusy)
    {
        if (materiel.Count == 0 && gpuBusy.Count == 0) return GpuLimiterObserved.Inconnu;

        var raisonsLues = materiel.Select(m => m.RaisonsVues).OfType<ClocksEventReason>().ToList();
        ClocksEventReason? raisons = raisonsLues.Count == 0
            ? null
            : raisonsLues.Aggregate(ClocksEventReason.Aucune, (a, b) => a | b);

        var medianes = materiel.Select(m => m.FrequenceMedianeMhz).OfType<double>().ToList();
        var minimums = materiel.Select(m => m.FrequenceMinimaleMhz).OfType<double>().ToList();

        return GpuLimiter.Classer(
            raisons,
            gpuBusy.Count > 0 ? Mediane(gpuBusy) / 100.0 : null,
            medianes.Count > 0 ? Mediane(medianes) : null,
            minimums.Count > 0 ? minimums.Min() : null);
    }

    private static double Mediane(List<double> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>Ce que la session a pu voir, en francais simple : « ta partie etait trop bruitee » plutot que « ton
    /// produit ne sert a rien ». Elle dit la regle qui garde un reglage et, quand la partie a pu decider, ce que
    /// l'endroit permet de conclure (`SceneContract`) : une scene inconnue ne vaut pas une pleine partie.</summary>
    private static string Expliquer(SceneResolution scene, SceneContract contrat)
        => scene.Verdict switch
    {
        SceneVerdict.DriftDominant =>
            Docteur.T("GOS.Derive", scene.DriftPctPerRun),

        SceneVerdict.TooFewRuns =>
            Docteur.T("GOS.PassesInsuffisantes"),

        // La règle qui décide (KeepPolicy.DecideParMetrique), pas des poids qu'aucun verdict n'applique ni la résolution de
        // l'A/A, plus large que celle de l'A/B apparié qui juge : chaque verdict dit sa propre marge.
        _ => Docteur.T("GOS.Resolution")
             + (contrat.CeQuiManque is { } manque ? Docteur.T("GOS.Attention", manque) : string.Empty),
    };
}
