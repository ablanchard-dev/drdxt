namespace DrDxt.Core;

/// <summary>
/// PHASE 18 F6 (L8 b) — ce que la mesure du SCAN dit a l'Apply, reduit a ce dont le choix a besoin.
/// Pur : les lectures (journal WER, arrets brutaux) sont faites par l'appelant.
/// </summary>
public static class DiagnosticDeScan
{
    // 06/10/2026 (L-GPU) : le drapeau global `TuningGpuAutorise = false` (decision du 07/09, prise pour la seule machine
    // du mainteneur) est retire. La carte se ferme machine par machine, par famille d'arret
    // (`CandidateSelector.CarteFermeeParLesArrets`), et un reglage n'est garde qu'avec un gain mesure en jeu.

    /// <param name="pressionThread">Ce que les threads du jeu ont CONSOMME pendant la fenetre de mesure
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
/// PHASE 18 (branchement, revue du 10/09/2026) — la CONSOMMATION du thread de jeu le plus chaud,
/// reduite a ce que <see cref="CandidateSelector.ClasserFamilles"/> sait lire.
///
/// `GameThreadPressure` existait, et l'App le laissait a Unknown : le diagnostic ne pouvait jamais
/// faire remonter les familles CPU/Scheduler. La mesure vient de `ThreadCpuReader` (GetThreadTimes,
/// espace utilisateur, sans elevation) pendant la fenetre de mesure du scan — la meme que les frames.
///
/// Sur un CPU hybride, « proche d'un thread logique » ne dit rien du type de coeur (P ou E) : le
/// thread a pu tourner sur un E-core ou migrer. C'est une observation, jamais une cause.
/// </summary>
public static class PressionThreads
{
    /// <summary>A partir de quelle part d'UN thread logique le thread le plus chaud est « proche
    /// d'un thread ».
    ///
    /// **NON CHIFFRÉ.** Aucune source ni aucun outil du depot ne fixe ce seuil (`tools/cpuprobe`
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
/// PHASE 18 F6 (L8 b) — juger avant d'encaisser. Ce que l'Optimiser fera sur cette machine, decide
/// avant tout paiement : un client a qui il n'y a rien a tenter ne paie pas pour l'apprendre.
/// </summary>
/// <param name="Appreciations">Chaque graine, jugee, avec ses motifs.</param>
/// <param name="LotAChaud">tous les leviers mesurables que le selecteur retient, dans l'ordre des
/// familles que la mesure met en tete. Vide = aucun. La campagne P3 (`tools/optimize`) tente ce lot
/// entier, un candidat a la fois, et l'App son premier ; un levier ECARTE (veto des arrets brutaux
/// compris) n'y entre jamais, parce que le lot est construit ici, sur les memes appreciations que
/// l'ecran.</param>
/// <param name="AuRedemarrage">Les corrections dont l'effet n'existe qu'apres redemarrage, retenues
/// par tous les vetos du selecteur sauf la nature (un clic de l'utilisateur n'est pas « l'autopilote
/// de lui-meme »).</param>
/// <param name="SansDescripteur">Des graines que le catalogue ne connait pas : jamais tentees.</param>
/// <param name="VetoDesArrets">Le motif quand les arrets brutaux forment une condition, sinon
/// <c>null</c>. Il ne DECIDE rien : le selecteur a deja ferme chaque surface materielle ou pilote
/// (<see cref="CandidateSelector.FermeeParLesArrets"/>). Il sert a le dire — l'ecran de l'App
/// (« un test memoire d'abord ») et l'outil de campagne.
///
/// 11/09/2026 : il s'appelait `VetoDeCampagne` et VIDAIT le lot de la seule campagne, parce que
/// trois leviers du pilote passaient le veto du selecteur (ils se declaraient « latence »).
/// L'Optimiser de l'App, lui, tentait encore `latency.reflex`. Le defaut etait dans le veto, pas
/// chez l'appelant : il ferme desormais sur la SURFACE ecrite, et ce second veto par appelant, devenu
/// redondant, est retire.</param>
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

    /// <summary>06/10/2026 (L-WU) : les réglages posés au clic, effectifs tout de suite mais sans effet sur les images
    /// (<see cref="CandidateKind.AutoReglage"/>) : appliqués avec ceux du redémarrage, sans mesure.</summary>
    public IReadOnlyList<string> PosesSansMesure { get; init; } = [];

    public bool RienATenter => LotAChaud.Count == 0 && AuRedemarrage.Count == 0 && PosesSansMesure.Count == 0;

    /// <param name="jeuEnCours">Un jeu tournait-il au scan ? Sans lui, un levier a chaud etait
    /// « a tenter » sans jeu : le client payait et creait un point de restauration pour lire ensuite
    /// « lance ton jeu » (revue du 10/09). Un levier qui se mesure en jeu, sans jeu, n'est pas
    /// a tenter — et ce refus est gratuit.</param>
    public static JugementApply Juger(DiagnosticMachine diagnostic, IReadOnlyList<string> graines, bool jeuEnCours)
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
            // L-WU : un réglage sans effet sur les images n'a rien à voir avec le goulot ; son motif le dit.
            .Select((a, i) => a.Retenu && descripteurs[i].Kind == CandidateKind.AutoReglage
                ? new CandidateAppraisal(a.Id, true, ["posé au clic, sans effet sur les images : il ne se mesure pas en jeu"])
                : a)
            .ToList();

        if (!jeuEnCours)
            appreciations = appreciations
                .Select((a, i) => a.Retenu && descripteurs[i].Kind == CandidateKind.AutoHot
                    ? new CandidateAppraisal(a.Id, false, new[]
                    {
                        "se mesure en jeu, et aucun jeu n'était lancé au scan : lance ton jeu, refais un scan",
                    })
                    : a)
                .ToList();

        // 07/10/2026 (revue F-16) : changer le mode d'écran sort un jeu plein écran exclusif, et la question « garder ou
        // revenir » reste derrière lui. L'affichage se règle hors partie.
        if (jeuEnCours)
            appreciations = appreciations
                .Select((a, i) => a.Retenu && descripteurs[i].Surface == SurfaceTouchee.Affichage
                    ? new CandidateAppraisal(a.Id, false, ["change l'affichage : à poser jeu fermé, pour que la question « garder ou revenir » se voie"])
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
