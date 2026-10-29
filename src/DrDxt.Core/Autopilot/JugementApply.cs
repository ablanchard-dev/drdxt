namespace DrDxt.Core;

/// <summary>
/// Ce que la mesure du scan dit a l'Apply, reduit a ce dont le choix a besoin.
/// Pur : les lectures (journal WER, arrets brutaux) sont faites par l'appelant.
/// </summary>
public static class DiagnosticDeScan
{
    /// <summary>Aucun drapeau global ne ferme la carte : elle passe sous la garde d'incident, les arrets ne ferment que les
    /// leviers qui ne declarent pas ce qu'ils ecrivent (`CandidateSelector.CarteFermeeParLesArrets`), et un reglage n'est
    /// garde qu'avec un gain mesure en jeu.</summary>
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
/// La consommation du thread de jeu le plus chaud, reduite a ce que <see cref="CandidateSelector.ClasserFamilles"/> lit
/// pour faire remonter les familles CPU/Scheduler. Mesuree par `ThreadCpuReader` (GetThreadTimes, sans elevation) sur la
/// fenetre de mesure du scan. Sur un CPU hybride, elle ne dit rien du type de coeur (P ou E) : une observation, jamais
/// une cause.
/// </summary>
public static class PressionThreads
{
    /// <summary>La part d'un thread logique a partir de laquelle le plus chaud est « proche d'un thread ». Non chiffré :
    /// aucune source ne le fixe (`tools/cpuprobe` n'affiche que des parts) ; 0,90 laisse 10 % a l'ordonnanceur et au bruit
    /// de lecture.</summary>
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
/// <param name="LotAChaud">Les leviers mesurables retenus, dans l'ordre des familles que la mesure met en tete ; vide =
/// aucun. La campagne (`tools/optimize`) tente le lot, un a la fois, l'App son premier ; construit sur les appreciations
/// de l'ecran, il n'admet aucun levier ecarte.</param>
/// <param name="AuRedemarrage">Les corrections effectives apres redemarrage, retenues par tous les vetos sauf la nature
/// (un clic n'est pas « l'autopilote de lui-meme »).</param>
/// <param name="SansDescripteur">Des graines que le catalogue ne connait pas : jamais tentees.</param>
/// <param name="VetoDesArrets">Le motif quand les arrets brutaux forment une condition, sinon <c>null</c>. Il ne decide
/// rien (<see cref="CandidateSelector.CarteFermeeParLesArrets"/> le fait) : il sert a le dire (« un test memoire
/// d'abord »).</param>
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

    /// <param name="jeuEnCours">Un jeu tournait-il au scan ? Sans jeu, un levier mesure en jeu n'est pas a tenter (un
    /// point de restauration pour lire « lance ton jeu »).</param>
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

        // Changer le mode d'écran sort un jeu plein écran exclusif, « garder ou revenir » restant derrière lui :
        // l'affichage se règle hors partie, jeu lancé après le scan compris.
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
