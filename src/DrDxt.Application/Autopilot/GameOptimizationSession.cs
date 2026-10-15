using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>L'identite de ce qui a ete optimise. Un profil sans identite est un profil qu'on
/// appliquera un jour a une autre machine, un autre pilote ou une autre resolution, et qui
/// promettra un gain que plus rien ne soutient.</summary>
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

    /// <summary>Ce que l'A/A resout sur le 1 % low seul. <c>null</c> = pas assez de passes.
    ///
    /// Sans cette mesure, une session rend un chiffre de resolution et le joueur ne peut pas
    /// savoir laquelle des deux grandeurs la ruine. Mesure sur une session Warzone du 07/09/2026 : le score
    /// composite resolvait +/-74 %, et un candidat qui n'ecrit rien y est indiscernable d'un vrai
    /// gain. Separer les deux dit si la moyenne, elle, reste decidable.</summary>
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

    /// <summary>La capture de reference, telle quelle. Elle porte les ratios que le classifieur
    /// de goulot demande (GPU busy, attente GPU, travail CPU de l'app, latence, cadence).
    ///
    /// Sans elle, l'autopilote proposerait les memes leviers a une machine GPU-bound a 97 % et a
    /// une machine que le GPU attend. La session ne classe pas elle-meme : elle rend la mesure et
    /// laisse l'appelant classer, parce que le classifieur vit dans une couche qu'elle ne connait pas.</summary>
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
/// que ceux dont le gain se mesure au-dessus du bruit. Pour le joueur qui veut savoir ce qui aide sur son jeu.
///
/// C'est ici que les briques prouvees separement se rejoignent : le banc du produit
/// (<see cref="IBenchmarkRunner"/>, qui sait deja reparer son propre service de mesure), la
/// fonction objectif, l'A/A, le protocole apparie et la politique de conservation.
///
/// **Ce service ne refait pas le moteur, il le branche.** `AutopilotEngine` reste pur et
/// ignorant du jeu ; tout ce que cette classe lui apporte est une facon de mesurer une partie et
/// une identite a inscrire sur le profil.
///
/// ══ ce qui est mesure ══
/// Un score melange (`0,35 x moyenne + 0,65 x 1 % low`) ne se resout pas : mesure sur une session
/// Warzone reelle, il annoncait **+/-72 %** quand la moyenne seule resolvait a **+/-4,6 %**. Le
/// 1 % low ruinerait la mesure des deux sans le dire.
///
/// Une ponderation est une preference du joueur ; ce n'est pas une permission de transformer une
/// grandeur inconnue en information. Chaque passe rend donc toutes ses grandeurs, chacune est
/// jugee separement (<see cref="GameMetricSet"/>), et l'arbitrage vient apres, sur des preuves
/// qui existent deja (<see cref="KeepPolicy.DecideParMetrique"/>).
///
/// ══ un resultat vide est un resultat ══
/// « Aucun reglage conserve : cette partie etait trop bruitee pour resoudre les gains
/// disponibles » est une reponse honnete et utile. C'est infiniment preferable a un faux +3 %.
/// </summary>
public sealed class GameOptimizationSession
{
    private readonly IBenchmarkRunner _bench;
    private readonly Func<GameProfileIdentity> _identite;

    /// <summary>
    /// Duree d'une passe. **10 s, et c'est une mesure, pas une intuition.**
    ///
    /// Comparer trois passes courtes a trois passes longues confond la duree de passe avec le
    /// temps total de jeu. A temps de jeu egal, l'arithmetique va dans l'autre sens. La marge inter-passes vaut
    /// `t95(n-2) x sigma / racine(n)` : passer de 3 a 17 passes fait chuter t95 de 4,30 a 2,13 et
    /// multiplie racine(n) par 2,4 : un facteur ~4,8 que l'augmentation de sigma sur des passes
    /// plus courtes ne compense pas.
    ///
    /// Mesure en rejouant huit captures Warzone du 01/09 a travers ce code, resolution mediane :
    ///   10 s → ~3,4 %     20 s → ~4,8 %     45 s → ~30 %
    /// Sept captures sur huit sont meilleures a 10 s qu'a toute autre duree.
    ///
    /// On pourrait craindre qu'a 10 s le 1 % low ne repose que sur ~14 frames. Le banc dit le
    /// contraire : a temps de jeu egal (240 s), `tools/tailbench` mesure sur 8 captures Warzone reelles
    ///
    ///   protocole                 moyenne   p95 frametime   1 % low
    ///   10 s x 24 alternances      2,7 %        3,3 %         8,7 %     ← meilleur partout
    ///   15 s x 16 alternances      3,4 %        3,2 %        10,1 %
    ///   20 s x 12 alternances      5,5 %        7,2 %        19,6 %
    ///   30 s x 8  alternances      3,9 %        4,6 %        18,2 %
    ///
    /// Le percentile d'une fenetre de 10 s est bien plus bruite, mais le modele apparie en moyenne
    /// vingt-quatre au lieu de seize. Le nombre de points bat la precision de chaque point, la
    /// meme arithmetique que pour la duree elle-meme.
    ///
    /// Details : `docs/PROTOCOLES-DE-MESURE-IN-GAME.md`.
    /// </summary>
    public static readonly TimeSpan DureeParPasse = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Plafond de passes d'alternance. **24, soit 4 minutes de jeu au pire, et souvent moins,**
    /// parce que l'experience s'arrete des qu'elle sait repondre.
    ///
    /// Le protocole s'arretait a quatre passes. Quatre passes ne laissent qu'un degre de
    /// liberte au modele (trois parametres a ajuster), donc t95 = 12,706 et une resolution
    /// enorme : le produit ne pouvait rien prouver sous ~14 %. Mesure du 07/09/2026, bruit vrai
    /// sur la moyenne : 13,7 % a 4 alternances, 5,2 % a 8, 3,9 % a 12, 2,7 % a 24.
    ///
    /// C'est un plafond, pas une duree. Une regression prouvee ou un gain de fluidite prouve
    /// terminent l'experience immediatement.
    /// </summary>
    public const int AlternancesMax = 24;

    /// <summary>Une passe = un run du banc. Le banc agrege deja plusieurs runs quand on le lui
    /// demande ; ici chaque passe doit rester un point independant dans le temps, sinon l'A/A
    /// mesurerait la dispersion interne d'une passe au lieu de celle de la scene.</summary>
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
    /// <param name="dureeParPasse">Duree d'une passe. <c>null</c> = <see cref="DureeParPasse"/>.
    /// La changer deplace la sensibilite dans les deux sens, et pas comme on le croit : voir
    /// <see cref="DureeParPasse"/>. A temps de jeu egal, des passes plus courtes resolvent mieux :
    /// mesure du 07/09/2026 sur huit captures reelles. Ce qui compte n'est pas la duree d'une
    /// passe mais le nombre de passes que le temps total permet.</param>
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

        // Une passe perdue pour cause d'alt-tab se refait, elle ne coute pas la session.
        // Mesure du 07/09/2026 sur Warzone : focus 100 % / 62 % / 100 % : une seule bascule du
        // joueur, et il ne restait que deux passes exploitables sur trois, donc aucune resolution
        // (il en faut trois). Le joueur avait pourtant joue. Le banc rattrape deja une passe sans
        // frame ; il n'y a aucune raison de traiter differemment une passe non jouee.
        //
        // Borne au nombre de passes demandees : sur une session ou le joueur est constamment
        // ailleurs, on rend ce qu'on a et on le dit, plutot que de mesurer sans fin.
        var rattrapagesFocus = Math.Max(3, passesAa);
        var derniereEcarteePourFocus = false;

        GameMetrics? Mesurer()
        {
            for (var essai = 0; ; essai++)
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

            // Le focus observe decide. Une passe passee derriere une autre fenetre ne dit rien
            // des performances en jeu : elle ne compte pas, exactement comme une passe sans frame.
            // Et le focus ne dit rien de la performance elle-meme : une passe entierement jouee
            // reste comptee, que le PC y ait ete brillant ou catastrophique.
            // Ce que le focus a reellement observe pendant cette passe. Trace au fil de la
            // session : sans elle, une passe ecartee et une passe jamais observee se ressemblent.
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

        // Laquelle des deux grandeurs ruine la resolution ?
        // Le moteur ne voit qu'un score composite ; s'il resout mal, il ne dit pas pourquoi. Les
        // memes passes d'A/A, reprobees separement, le disent : c'est presque toujours le 1 % low,
        // un percentile sur ~21 frames, qui disperse, et il entraine la moyenne avec lui dans le
        // score. Ces deux resolutions ne decident rien (le score decide, seul) : elles expliquent.
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
            Expliquer(profil.Scene, but, scene ?? SceneContract.Inconnue)
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

    /// <summary>Ce que la session a pu voir, en francais simple. C'est la phrase qui evite au
    /// joueur de conclure « ton produit ne sert a rien » quand la vraie reponse est « ta partie
    /// etait trop bruitee ».
    ///
    /// Elle dit ce que la partie permet de mesurer, et aussi ce que l'endroit permet de conclure
    /// (`SceneContract`) : un reglage trouve sur une scene inconnue ne vaut pas un reglage valide
    /// en pleine partie, et la phrase le dit. Le contrat ne
    /// s'ajoute qu'au cas ou la partie a pu decider : quand rien n'a ete teste, il n'y a pas de
    /// keep a nuancer.</summary>
    private static string Expliquer(SceneResolution scene, OptimizationGoal but, SceneContract contrat)
        => scene.Verdict switch
    {
        SceneVerdict.DriftDominant =>
            Docteur.T("GOS.Derive", scene.DriftPctPerRun),

        SceneVerdict.TooFewRuns =>
            Docteur.T("GOS.PassesInsuffisantes"),

        _ => Docteur.T("GOS.Resolution", scene.ResolutionPct, GameObjective.Explication(but))
             + (contrat.CeQuiManque is { } manque ? Docteur.T("GOS.Attention", manque) : string.Empty),
    };
}
