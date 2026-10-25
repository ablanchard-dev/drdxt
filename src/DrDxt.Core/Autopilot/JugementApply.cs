namespace DrDxt.Core;

/// <summary>
/// Ce que la mesure du scan dit a l'Apply, reduit a ce dont le choix a besoin.
/// Pur : les lectures (journal WER, arrets brutaux) sont faites par l'appelant.
/// </summary>
public static class DiagnosticDeScan
{
    // Aucun drapeau global ne ferme la carte : elle passe sous la garde d'incident, les arrets ne ferment que les leviers
    // qui ne declarent pas ce qu'ils ecrivent (`CandidateSelector.CarteFermeeParLesArrets`), et un reglage n'est garde
    // qu'avec un gain mesure en jeu.

    /// <param name="pressionThread">Ce que les threads du jeu ont consomme pendant la fenetre de mesure
    /// du scan (<see cref="PressionThreads"/>). Unknown = pas observe, et « pas observe » ne ferme rien.</param>
    public static DiagnosticMachine Construire(
        BottleneckVerdict? goulot,
        BenchmarkResult? mesure,
        InstabiliteGpuObservee? instabiliteGpu,
        IReadOnlyList<ArretClasse>? arretsBrutaux,
        GameThreadPressure pressionThread = GameThreadPressure.Unknown)
    {
        var materiel = mesure?.Materiel;
        return new DiagnosticMachine(
            // Un verdict provisoire n'etablit rien : il vaut « goulot non etabli ».
            Goulot: goulot is { Provisional: false } g ? g.State : BottleneckState.UnknownPartial,
            Bridage: mesure is null
                ? GpuLimiterObserved.Inconnu
                : GpuLimiter.Classer(materiel?.RaisonsVues, mesure.GpuBusyRatio,
                    materiel?.FrequenceMedianeMhz, materiel?.FrequenceMinimaleMhz),
            FrequenceMedianeMhz: materiel?.FrequenceMedianeMhz,
            FrequenceMinimaleMhz: materiel?.FrequenceMinimaleMhz,
            TuningGpuAutorise: true,
            PressionThread: pressionThread,
            InstabiliteGpu: instabiliteGpu,
            ArretsBrutaux: arretsBrutaux);
    }
}

/// <summary>
/// La consommation du thread de jeu le plus chaud,
/// reduite a ce que <see cref="CandidateSelector.ClasserFamilles"/> sait lire.
///
/// `GameThreadPressure` existait, et l'App le laissait a Unknown : le diagnostic ne pouvait jamais
/// faire remonter les familles CPU/Scheduler. La mesure vient de `ThreadCpuReader` (GetThreadTimes,
/// espace utilisateur, sans elevation) pendant la fenetre de mesure du scan, la meme que les frames.
///
/// Sur un CPU hybride, « proche d'un thread logique » ne dit rien du type de coeur (P ou E) : le
/// thread a pu tourner sur un E-core ou migrer. C'est une observation, jamais une cause.
/// </summary>
public static class PressionThreads
{
    /// <summary>A partir de quelle part d'un seul thread logique le thread le plus chaud est « proche
    /// d'un thread ».
    ///
    /// **Non chiffré.** Aucune source ni aucun outil du depot ne fixe ce seuil (`tools/cpuprobe`
    /// n'affiche que des parts). 0,90 laisse 10 % a l'ordonnanceur et au bruit de lecture ; il est
    /// ecrit ici pour etre discute, comme les seuils du selecteur.</summary>
    public const double SeuilProcheDUnThread = 0.90;

    /// <param name="partDuThreadLePlusChaud">Sa consommation en part d'un thread logique ; null = non lu.</param>
    /// <param name="threadsVus">Combien de threads ont pu etre mesures ; 0 = rien de lisible.</param>
    public static GameThreadPressure Classer(double? partDuThreadLePlusChaud, int threadsVus)
        => partDuThreadLePlusChaud is not { } part || threadsVus <= 0 || !double.IsFinite(part)
            ? GameThreadPressure.Unknown
            : part >= SeuilProcheDUnThread ? GameThreadPressure.Observee : GameThreadPressure.Aucune;
}

/// <summary>
/// Juger avant d'agir. Ce que l'Optimiser fera sur cette machine, decide avant le clic : un joueur a qui il n'y a rien
/// a tenter ne lance pas une session pour l'apprendre.
/// </summary>
/// <param name="Appreciations">Chaque graine, jugee, avec ses motifs.</param>
/// <param name="LotAChaud">tous les leviers mesurables que le selecteur retient, dans l'ordre des
/// familles que la mesure met en tete. Vide = aucun. La campagne (`tools/optimize`) tente ce lot
/// entier, un candidat a la fois, et l'App son premier ; un levier ecarte (veto des arrets brutaux
/// compris) n'y entre jamais, parce que le lot est construit ici, sur les memes appreciations que
/// l'ecran.</param>
/// <param name="AuRedemarrage">Les corrections dont l'effet n'existe qu'apres redemarrage, retenues
/// par tous les vetos du selecteur sauf la nature (un clic de l'utilisateur n'est pas « l'autopilote
/// de lui-meme »).</param>
/// <param name="SansDescripteur">Des graines que le catalogue ne connait pas : jamais tentees.</param>
/// <param name="VetoDesArrets">Le motif quand les arrets brutaux forment une condition, sinon
/// <c>null</c>. Il ne decide rien : les arrets ne ferment que les leviers qui ne disent pas ce qu'ils ecrivent
/// (<see cref="CandidateSelector.CarteFermeeParLesArrets"/>), sur la surface ecrite, chez le selecteur seul. Il sert a le
/// dire : l'ecran de l'App (« un test memoire d'abord ») et l'outil de campagne.</param>
public sealed record JugementApply(
    IReadOnlyList<CandidateAppraisal> Appreciations,
    IReadOnlyList<CandidateDescriptor> LotAChaud,
    IReadOnlyList<string> AuRedemarrage,
    IReadOnlyList<string> SansDescripteur,
    string? VetoDesArrets = null)
{
    /// <summary>Le levier que l'Optimiser de l'App tente en session : le premier du lot. L'App n'en
    /// tente qu'un par clic ; `null` = aucun.</summary>
    public CandidateDescriptor? AChaud => LotAChaud.Count > 0 ? LotAChaud[0] : null;

    /// <summary>Les réglages posés au clic, effectifs tout de suite mais sans effet sur les images
    /// (<see cref="CandidateKind.AutoReglage"/>) : appliqués avec ceux du redémarrage, sans mesure.</summary>
    public IReadOnlyList<string> PosesSansMesure { get; init; } = [];

    public bool RienATenter => LotAChaud.Count == 0 && AuRedemarrage.Count == 0 && PosesSansMesure.Count == 0;

    /// <param name="jeuEnCours">Un jeu tournait-il au scan ? Un levier qui se mesure en jeu, sans jeu, n'est pas a tenter :
    /// le proposer creerait un point de restauration pour lire ensuite « lance ton jeu ».</param>
    /// <param name="jeuOuvert">Un jeu est-il ouvert maintenant (relu au clic, pas au scan) ? Par défaut, celui du scan.</param>
    public static JugementApply Juger(DiagnosticMachine diagnostic, IReadOnlyList<string> graines, bool jeuEnCours, bool? jeuOuvert = null)
    {
        var sansDescripteur = new List<string>();
        var descripteurs = new List<CandidateDescriptor>();
        foreach (var id in graines.Distinct(StringComparer.Ordinal))
        {
            if (CatalogueDeCandidats.Decrire(id) is { } d) descripteurs.Add(d);
            else sansDescripteur.Add(id);
        }

        var appreciations = CandidateSelector.Evaluer(diagnostic, descripteurs
            .Select(d => d.Kind is CandidateKind.AutoReboot or CandidateKind.AutoReglage ? d with { Kind = CandidateKind.AutoHot } : d)
            .ToList())
            // Un réglage sans effet sur les images n'a rien à voir avec le goulot ; son motif le dit.
            .Select((a, i) => a.Retenu && descripteurs[i].Kind == CandidateKind.AutoReglage
                ? new CandidateAppraisal(a.Id, true, [Docteur.T("JA.SansEffetImages")])
                : a)
            .ToList();

        if (!jeuEnCours)
            appreciations = appreciations
                .Select((a, i) => a.Retenu && descripteurs[i].Kind == CandidateKind.AutoHot
                    ? new CandidateAppraisal(a.Id, false, new[]
                    {
                        Docteur.T("JA.AucunJeu"),
                    })
                    : a)
                .ToList();

        // Changer le mode d'écran sort un jeu plein écran exclusif, et la question « garder ou
        // revenir » reste derrière lui. L'affichage se règle hors partie : un jeu lancé après le scan compte.
        if (jeuOuvert ?? jeuEnCours)
            appreciations = appreciations
                .Select((a, i) => a.Retenu && descripteurs[i].Surface == SurfaceTouchee.Affichage
                    ? new CandidateAppraisal(a.Id, false, [Docteur.T("JA.Affichage")])
                    : a)
                .ToList();

        // Les familles que la mesure met en tete passent d'abord ; a rang egal, l'ordre du plan.
        var rang = CandidateSelector.ClasserFamilles(diagnostic)
            .Select(f => f.Famille).Distinct().Select((f, i) => (f, i))
            .ToDictionary(x => x.f, x => x.i);

        var retenus = descripteurs.Where((_, i) => appreciations[i].Retenu).ToList();
        // OrderBy est stable : a rang egal, l'ordre du plan tient.
        var lotAChaud = retenus
            .Where(d => d.Kind == CandidateKind.AutoHot)
            .OrderBy(d => rang.TryGetValue(d.Family, out var r) ? r : int.MaxValue)
            .ToList();
        var auRedemarrage = retenus
            .Where(d => d.Kind == CandidateKind.AutoReboot)
            .Select(d => d.Id)
            .ToList();

        return new JugementApply(appreciations, lotAChaud, auRedemarrage, sansDescripteur,
            CandidateSelector.ArretsBrutauxBloquants(diagnostic, DateTimeOffset.UtcNow))
        {
            PosesSansMesure = retenus.Where(d => d.Kind == CandidateKind.AutoReglage).Select(d => d.Id).ToList(),
        };
    }
}
