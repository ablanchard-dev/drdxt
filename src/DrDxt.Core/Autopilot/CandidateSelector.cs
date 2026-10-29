namespace DrDxt.Core;

/// <summary>Ce que le diagnostic dit d'un candidat, avant de depenser le temps du joueur.</summary>
/// <param name="Id">Le candidat juge.</param>
/// <param name="Retenu">Vaut-il la peine d'etre essaye sur cette machine, maintenant ?</param>
/// <param name="Motifs">Pourquoi. Toujours au moins un : un refus sans motif est une autorite. Tous les motifs, pas le
/// premier trouve : un interdit d'ecriture peut cacher la raison la plus forte, mesuree (une frequence qui ne bouge pas
/// d'un MHz), et n'en dire qu'une donne une image fausse de ce que la machine autorise.</param>
public sealed record CandidateAppraisal(string Id, bool Retenu, IReadOnlyList<string> Motifs)
{
    /// <summary>Les motifs en une ligne, pour un affichage compact.</summary>
    public string Motif => string.Join(" · ", Motifs);
}

/// <summary>Ce que les threads du jeu ont montre. Une observation, jamais une cause.</summary>
public enum GameThreadPressure
{
    /// <summary>Pas mesure. Ne se confond pas avec « aucune pression ».</summary>
    Unknown,

    /// <summary>Mesure, et aucun thread ne s'approche durablement de la capacite d'un thread
    /// logique.</summary>
    Aucune,

    /// <summary>Un ou quelques threads consomment durablement une part proche d'un thread logique
    /// pendant que le GPU reste sous saturation. Sur un CPU hybride, ceci ne dit rien du type de
    /// coeur : le thread a pu tourner sur un E-core ou migrer.</summary>
    Observee,

    /// <summary>Les lectures sont trop instables ou trop partielles pour trancher.</summary>
    Suspectee,
}

/// <summary>
/// Un incident noyau du sous-systeme graphique, deja repete a l'identique.
///
/// Une valeur a part, pas un `TuningGpuAutorise = false` de plus : ce drapeau porte une decision (« 0 MHz / 100 % »),
/// ceci une preuve, et lever l'une ne doit pas lever l'autre.
/// </summary>
/// <param name="Occurrences">Combien de fois la meme signature est revenue. Une seule fois est un
/// accident ; plusieurs fois est une condition.</param>
/// <param name="PlusRecente">Le dernier incident. Un pilote a pu etre mis a jour depuis.</param>
/// <param name="Signature">De quoi le nommer sans l'interpreter, p. ex.
/// « 0x193 VIDEO_DXGKRNL_LIVEDUMP (parametre 1 : 0x80e) ».</param>
public sealed record InstabiliteGpuObservee(
    int Occurrences,
    DateTimeOffset PlusRecente,
    string Signature);

/// <summary>Ce que la machine a montre, reduit a ce dont le choix a besoin.</summary>
/// <param name="Goulot">Qui donne le tempo, selon le classifieur.</param>
/// <param name="Bridage">Ce que la telemetrie a observe pendant les fenetres mesurees.</param>
/// <param name="FrequenceMedianeMhz">Frequence GPU mediane, ou <c>null</c> si non lue.</param>
/// <param name="FrequenceMinimaleMhz">Frequence GPU minimale, ou <c>null</c> si non lue.</param>
/// <param name="TuningGpuAutorise">Faux quand une decision de l'utilisateur interdit d'ecrire sur
/// la carte, par exemple une machine qu'on debogue pour des arrets non propres.</param>
public sealed record DiagnosticMachine(
    BottleneckState Goulot,
    GpuLimiterObserved Bridage,
    double? FrequenceMedianeMhz,
    double? FrequenceMinimaleMhz,
    bool TuningGpuAutorise,
    GameThreadPressure PressionThread = GameThreadPressure.Unknown,
    InstabiliteGpuObservee? InstabiliteGpu = null,
    // Les arrets brutaux (Kernel-Power 41), plus recents d'abord. null = non lu, et
    // « non lu » ne ferme rien. Un arret sec ne laisse aucun dump : aucune reparation n'en
    // creera un, et le prochain incident apres une ecriture materielle serait inattribuable.
    // Chaque arret porte sa famille ; une coupure en veille ne ferme rien.
    IReadOnlyList<ArretClasse>? ArretsBrutaux = null);

/// <summary>
/// Ecarte, avant toute partie mesuree (quatre minutes et demie de jeu chacune), les leviers que la mesure disqualifie
/// deja, et dit pourquoi. Il elimine, il ne choisit pas le meilleur, et il ne prouve rien : aucun keep, aucun gain
/// annonce, la preuve reste a l'experience appariee.
/// <list type="bullet">
/// <item>Inerte : forcer l'etat P maximal n'a de sens que si la carte redescend ; mesure sur Warzone le 08/09/2026, mediane et
/// minimum a 2715 MHz, aucun bridage publie.</item>
/// <item>Interdit : une decision de l'utilisateur (« 0 MHz / 100 % tant que les arrets ne sont pas expliques ») ferme
/// la famille, sans etre repesee a chaque session.</item>
/// <item>Hors sujet : un levier GPU sur une machine CPU-bound est dit peu prometteur, pas interdit (le classifieur peut
/// se tromper).</item>
/// </list>
/// </summary>
public static class CandidateSelector
{
    /// <summary>Sous cette variation, la frequence GPU est consideree comme plate : forcer un
    /// etat P n'a alors rien a donner.
    ///
    /// **Non chiffré.** 2 % laisse passer le jitter de lecture d'une sonde echantillonnee a
    /// 250 ms et arrete un vrai palier. Il est ecrit ici pour etre discute.</summary>
    public const double VariationFrequencePlate = 0.02;

    /// <summary>Les familles reconnues, telles que les descripteurs les nomment. Un candidat qui
    /// ne declare aucune de ces grandeurs n'est pas juge sur sa famille, seulement sur sa
    /// nature et sur les interdits.</summary>
    public const string GrandeurFrequenceGpu = "gpu.clock";
    public const string GrandeurPuissanceGpu = "gpu.power";
    public const string GrandeurLatence = "latence";

    /// <summary>
    /// Les familles, de la plus prometteuse a la moins, avant tout choix d'identifiant : la question est « ou cette
    /// machine perd-elle de la performance ? », le catalogue vient apres. Une famille en tete sans aucun levier dit quoi
    /// construire. Un classement ordonne des depenses de temps de jeu ; ce n'est pas une preuve.
    /// </summary>
    public static IReadOnlyList<(CandidateFamily Famille, string Motif)> ClasserFamilles(
        DiagnosticMachine d)
    {
        var rang = new List<(CandidateFamily, string)>();

        // Le GPU n'est pas sature et un thread de jeu est sous pression : ce qui manque n'est pas
        // de la puissance GPU, c'est de quoi l'alimenter.
        if (d.PressionThread == GameThreadPressure.Observee
            && d.Goulot is BottleneckState.MixedGpuLeaning or BottleneckState.CpuBoundOrAppBound
                        or BottleneckState.CpuBoundStrong)
        {
            rang.Add((CandidateFamily.Cpu, Docteur.T("CS.Rang.Cpu")));
            rang.Add((CandidateFamily.Scheduler, Docteur.T("CS.Rang.Scheduler")));
        }

        // Un plafond publie oriente vers la famille thermique (la courbe de ventilation, fermee aujourd'hui).
        if (d.Bridage is GpuLimiterObserved.PlafondPuissanceObserve
                      or GpuLimiterObserved.PlafondThermiqueObserve)
            rang.Add((CandidateFamily.Thermal, Docteur.T("CS.Rang.Thermal")));

        if (d.Goulot == BottleneckState.RamCommitPressure)
            rang.Add((CandidateFamily.Memory, Docteur.T("CS.Rang.Memory")));

        if (d.Goulot == BottleneckState.LatencyOnly)
            rang.Add((CandidateFamily.Latency, Docteur.T("CS.Rang.Latency")));

        if (d.Goulot == BottleneckState.GpuBound)
            rang.Add((CandidateFamily.Gpu, Docteur.T("CS.Rang.Gpu")));

        // La reparation ferme la marche quand rien n'est prouve ailleurs : elle ne demande aucune
        // mesure de jeu et elle nettoie le terrain sur lequel toutes les autres mesureront.
        rang.Add((CandidateFamily.Repair, Docteur.T("CS.Rang.Repair")));

        return rang;
    }

    /// <summary>Juge chaque candidat contre ce que la machine a montre.</summary>
    public static IReadOnlyList<CandidateAppraisal> Evaluer(
        DiagnosticMachine diagnostic,
        IReadOnlyList<CandidateDescriptor> candidats)
    {
        var sortie = new List<CandidateAppraisal>();
        foreach (var c in candidats ?? Array.Empty<CandidateDescriptor>())
            sortie.Add(Juger(diagnostic, c));
        return sortie;
    }

    private static CandidateAppraisal Juger(DiagnosticMachine d, CandidateDescriptor c)
    {
        // On accumule, on ne rend pas au premier motif. Un candidat peut etre disqualifie pour
        // plusieurs raisons independantes, et n'en dire qu'une donne une image fausse de ce que la
        // machine autorise, celle qu'on lira dans six mois pour decider si la regle etait bonne.
        var refus = new List<string>();
        var touche = c.AffectedMetrics ?? Array.Empty<string>();
        var toucheGpu = touche.Any(m => m.StartsWith("gpu.", StringComparison.Ordinal));

        // ── 1. Sa nature ────────────────────────────────────────────────────────────────────
        // Rien a voir avec le diagnostic : l'autopilote n'ecrit pas dans un BIOS et ne reboote
        // pas la machine de quelqu'un.
        if (c.Kind != CandidateKind.AutoHot)
            refus.Add(Docteur.T("AE.Nature", RepairLibelles.Nature(c.Kind)));

        // ── 2. Les interdits de l'utilisateur ───────────────────────────────────────────────
        // Une decision prise n'est pas une preference a repeser a chaque session.
        // Sur la surface ecrite, comme les deux vetos, et non sur les grandeurs `gpu.` declarees. Voir `FermeeParLInterditGpu`.
        if (FermeeParLInterditGpu(c.Surface) && !d.TuningGpuAutorise)
            refus.Add(Docteur.T("CS.GpuInterdit"));

        // ── 2 bis. Ce que la stabilite interdit ─────────────────────────────────────────────
        // Ecrire sur une carte dont le noyau graphique a deja crie plusieurs fois rend le
        // prochain incident inattribuable : il pourra toujours venir de notre reglage ou de la
        // condition preexistante. On perd alors la seule chose que ce produit vend, l'attribution.
        // Sur la surface ecrite, et non sur les grandeurs `gpu.` : un levier qui se dit « latence » peut ecrire le pilote.
        // Voir `FermeeParInstabiliteGraphique`.
        if (FermeeParInstabiliteGraphique(c.Surface) && InstabiliteBloquante(d, DateTimeOffset.UtcNow) is { } instable)
            refus.Add(instable);

        // Les arrêts passés ne ferment ni le matériel ni le pilote : la garde d'incident (Application, GardeDIncident) rend
        // tout réglage suivi d'un arrêt hors veille dans la semaine, donc l'incident suivant n'est pas inattribuable, il
        // retire ce qui a été posé juste avant. Seul un levier qui ne déclare pas ce qu'il écrit reste fermé par ces
        // arrêts, par famille d'arrêt : rien ne le relierait à la garde.
        if (c.Surface == SurfaceTouchee.NonDeclaree && CarteFermeeParLesArrets(d, DateTimeOffset.UtcNow) is { } carte)
            refus.Add(carte);

        // Tension, fréquence et ventilateur de la carte ne s'écrivent pas : la carte rouverte
        // ne rouvre que la limite de puissance. Décalage d'horloge et courbe de ventilation restent fermés partout.
        if (ReglagesCarteInterdits.Contains(c.Id))
            refus.Add(Docteur.T("CS.RegleDeLaCarte"));

        // ── 3. Ce que la mesure disqualifie ─────────────────────────────────────────────────
        // Le cœur. Forcer la frequence n'a de sens que si elle redescend.
        if (touche.Contains(GrandeurFrequenceGpu, StringComparer.Ordinal)
            && FrequencePlate(d) is { } plate)
            refus.Add(plate);

        // Le levier de puissance abaisse la limite (moins de chaleur). Une carte qui bute deja
        // sur la sienne perdrait des images a coup sur ; ailleurs, l'essai coute peu et la mesure tranche.
        if (touche.Contains(GrandeurPuissanceGpu, StringComparer.Ordinal)
            && d.Bridage == GpuLimiterObserved.PlafondPuissanceObserve)
            refus.Add(Docteur.T("CS.DejaALaLimite"));

        // ── 4. Ce que l'appelant a deja constate ────────────────────────────────────────────
        // L'applicabilite reelle d'un levier (`AppliesTo`, une valeur d'origine illisible) se lit
        // sur la machine, pas ici. Quand l'appelant l'a lue, elle compte comme un refus a part
        // entiere : c'est meme le plus fort de tous, puisqu'il dit que l'ecriture echouerait.
        if (c.BlocageConstate is { Length: > 0 } blocage)
            refus.Add(blocage);

        if (refus.Count > 0) return new CandidateAppraisal(c.Id, false, refus);

        // ── 5. Le pronostic, qui n'interdit rien ────────────────────────────────────────────
        // Le classifieur peut se tromper, et une mesure vaut mieux qu'un pronostic : on essaie,
        // en disant que l'espoir est mince.
        if (toucheGpu && d.Goulot == BottleneckState.CpuBoundStrong)
            return new CandidateAppraisal(c.Id, true, new[]
            {
                Docteur.T("CS.GpuSurCpuBound"),
            });

        return new CandidateAppraisal(c.Id, true, new[] { MotifRetenu(d, c) });
    }

    /// <summary>
    /// L'instabilite graphique observee interdit-elle d'ecrire sur la carte ? Rend le motif quand
    /// oui, <c>null</c> quand elle ne suffit pas, y compris quand elle n'a pas ete lue, parce que
    /// « non lu » n'est pas « aucun incident ».
    /// </summary>
    public static string? InstabiliteBloquante(DiagnosticMachine d, DateTimeOffset maintenant)
        => IncidentsCarte(d, maintenant) is { } incidents
            ? Docteur.T("CS.Inattribuable", incidents)
            : null;

    /// <summary>La condition du pilote, dite sans la raison d'un refus qui n'a plus lieu que pour une surface
    /// non déclarée ; la garde d'incident rend seule un réglage de la carte suivi d'un vidage à chaud.</summary>
    public static string? IncidentsCarte(DiagnosticMachine d, DateTimeOffset maintenant)
    {
        if (d.InstabiliteGpu is not { } i) return null;

        // Une seule fois est un accident : on ne ferme pas une famille entiere la-dessus.
        if (i.Occurrences < OccurrencesQuiFontUneCondition) return null;

        // Et une condition ancienne n'est pas une condition actuelle : un pilote a pu etre mis
        // a jour depuis. Passe la fenetre, l'incident reste un fait a afficher, pas un veto.
        var age = maintenant - i.PlusRecente;
        if (age > FenetreInstabilite) return null;

        return Docteur.T("CS.IncidentsNoyau", i.Occurrences, FenetreInstabilite.Days, i.Signature, i.PlusRecente.ToLocalTime());
    }

    /// <summary>
    /// Les arrets brutaux forment-ils une condition ? Memes seuils que l'instabilite
    /// graphique, non chiffres et ecrits pour etre discutes : une fois est un accident, plusieurs
    /// fois dans la fenetre est une condition. Rend le motif, ou <c>null</c>, y compris quand le
    /// journal n'a pas ete lu, parce que « non lu » n'est pas « la machine s'arrete ».
    /// </summary>
    internal static string? ArretsBrutauxBloquants(DiagnosticMachine d, DateTimeOffset maintenant)
    {
        if (d.ArretsBrutaux is not { } arrets) return null;

        // Une coupure pendant la veille ou la sortie de veille ne dit rien de la stabilite en charge.
        var recents = arrets.Count(a => a.Famille != FamilleDArret.Veille && maintenant - a.Quand <= FenetreInstabilite);
        if (recents < OccurrencesQuiFontUneCondition) return null;

        // Plus « un arret sec ne laisse aucun dump » : les arrets comptes ici peuvent etre des ecrans bleus, qui en laissent.
        // Ce motif ne refuse plus rien (voir Evaluer) ; il se dit, avec la garde qui rend un réglage suivi d'un arrêt.
        return Docteur.T("CS.ArretsSousGarde", recents, FenetreInstabilite.Days);
    }

    // Combien sont tombés pendant une partie, et de quel jeu.
    private static string SousCharge(IReadOnlyList<ArretClasse> arrets)
        => arrets.Where(a => a.PendantLaPartieDe is not null).ToList() is { Count: > 0 } enPartie
            ? Docteur.T("CS.DontEnPartie", enPartie.Count, string.Join(", ", enPartie.Select(a => a.PendantLaPartieDe).Distinct()))
            : "";

    /// <summary>
    /// Les arrêts qui
    /// ferment le réglage de la carte sur cette machine : un écran bleu graphique récent (une fois suffit, il désigne
    /// la carte), ou des arrêts secs sans code répétés (ils ne laissent aucun vidage : un incident après un réglage
    /// serait inattribuable). Ne ferment rien : une coupure en veille, un écran bleu d'un autre code (il laisse son
    /// code), un arrêt hors de la fenêtre. Rend le motif, ou <c>null</c> ; « non lu » ne ferme rien.
    /// </summary>
    public static string? CarteFermeeParLesArrets(DiagnosticMachine d, DateTimeOffset maintenant)
        => ArretsDeLaCarte(d, maintenant, avecRaison: true);

    // Sous garde, la phrase nomme ces arrêts sans la raison d'une fermeture qui n'a plus lieu.
    private static string? ArretsDeLaCarte(DiagnosticMachine d, DateTimeOffset maintenant, bool avecRaison)
    {
        if (d.ArretsBrutaux is not { } arrets) return null;
        var recents = arrets.Where(a => maintenant - a.Quand <= FenetreInstabilite).ToList();

        var graphiques = recents.Where(a => a.Famille == FamilleDArret.EcranBleuGraphique).OrderByDescending(a => a.Quand).ToList();
        if (graphiques.Count > 0)
        {
            var dernier = graphiques[0];
            var nom = ClasseurDArrets.CodesGraphiques.TryGetValue(dernier.CodeArret, out var n) ? " " + n : "";
            return Docteur.T(graphiques.Count == 1 ? "CS.ArretGraphique.Un" : "CS.ArretGraphique.Plusieurs", graphiques.Count,
                       ClasseurDArrets.Nom(FamilleDArret.EcranBleuGraphique), FenetreInstabilite.Days, $"0x{dernier.CodeArret:X}{nom}", dernier.Quand.ToLocalTime())
                   + (avecRaison ? Docteur.T("CS.ArretGraphique.Raison") : "");
        }

        var secs = recents.Where(a => a.Famille == FamilleDArret.SansCode).ToList();
        if (secs.Count >= OccurrencesQuiFontUneCondition)
            return Docteur.T("CS.ArretsSecs", secs.Count, ClasseurDArrets.Nom(FamilleDArret.SansCode), FenetreInstabilite.Days,
                       secs.Max(a => a.Quand).ToLocalTime(), SousCharge(secs))
                   + (avecRaison ? Docteur.T("CS.ArretsSecs.Raison") : "");

        return null;
    }

    /// <summary>
    /// Ce que les arrêts et les incidents récents font au réglage de la carte sur cette machine, en une phrase, avec la
    /// famille d'arrêt en cause : ils la mettent sous garde d'incident, ils ne la ferment pas.
    /// </summary>
    public static string PhraseReglageCarte(DiagnosticMachine d, DateTimeOffset maintenant)
    {
        // Les incidents du pilote mettent la carte sous garde, ils ne la ferment plus.
        if (IncidentsCarte(d, maintenant) is { } instable)
            return Docteur.T("CS.Carte.SousGardeIncidents", instable);
        // Ces arrêts ne ferment plus la carte, ils la mettent sous garde ; la phrase le dit.
        if (ArretsDeLaCarte(d, maintenant, avecRaison: false) is { } arrets)
            return Docteur.T("CS.Carte.SousGardeArrets", arrets);

        var regle = Docteur.T("CS.Carte.Regle");
        if (d.ArretsBrutaux is null)
            return Docteur.T("CS.Carte.JournalNonLu", regle);

        var ignores = d.ArretsBrutaux
            .Where(a => maintenant - a.Quand <= FenetreInstabilite)
            .GroupBy(a => a.Famille)
            .Select(g => Docteur.T("CS.Carte.Famille", g.Count(), ClasseurDArrets.Nom(g.Key)))
            .ToList();
        return ignores.Count == 0
            ? Docteur.T("CS.Carte.AucunArret", FenetreInstabilite.Days, regle)
            : Docteur.T("CS.Carte.ArretsSansLaCarte", string.Join(", ", ignores), regle);
    }

    /// <summary>Les réglages de la carte que DrDXT n'écrit jamais : fréquence (décalage d'horloge) et ventilateur.</summary>
    public static readonly IReadOnlySet<string> ReglagesCarteInterdits =
        new HashSet<string>(StringComparer.Ordinal) { "gpu.core_offset", "gpu.undervolt", "gpu.fan_curve" };

    /// <summary>
    /// Les surfaces que des incidents du noyau graphique ferment (TDR, 0x193 VIDEO_DXGKRNL_LIVEDUMP, nvlddmkm,
    /// LiveKernelReports) : `NonDeclaree` seule, un levier qui ne dit pas ce qu'il ecrit ne passant jamais un veto de
    /// stabilite. La carte et le pilote passent sous la garde d'incident (vidages a chaud surveilles une semaine apres la
    /// pose) ; le processeur, la memoire, l'OS utilisateur et ce qui n'ecrit rien restent ouverts. Une seule definition
    /// des surfaces : <see cref="SurfaceTouchee"/>.
    /// </summary>
    public static bool FermeeParInstabiliteGraphique(SurfaceTouchee surface) =>
        surface is SurfaceTouchee.NonDeclaree;

    /// <summary>
    /// Ce que la decision « 0 MHz / 100 % » (<see cref="DiagnosticMachine.TuningGpuAutorise"/> a
    /// faux) ferme, sur la meme definition des surfaces que les vetos : la carte elle-meme (frequences,
    /// tension, puissance, ventilateurs), et `NonDeclaree` (un levier qui ne dit pas ce qu'il ecrit ne passe
    /// jamais un interdit). `latency.prefer_max_perf` ecrit un reglage DRS du pilote (PREFERRED_PSTATE), ni horloge ni
    /// puissance : il reste ouvert ici, et les vetos de stabilite le ferment toujours.
    /// </summary>
    public static bool FermeeParLInterditGpu(SurfaceTouchee surface) =>
        surface is SurfaceTouchee.CarteGraphique or SurfaceTouchee.NonDeclaree;

    /// <summary>A partir de combien de fois la meme signature cesse d'etre un accident.
    ///
    /// **Non chiffré.** 2 est le plus petit nombre qui distingue « c'est revenu » de « c'est
    /// arrive ». Il est ecrit ici pour etre discute, comme <see cref="VariationFrequencePlate"/>.
    /// Le cout d'un refus de trop est une experience perdue ; le cout d'un refus manquant est un
    /// incident qu'on ne saura jamais attribuer.</summary>
    public const int OccurrencesQuiFontUneCondition = 2;

    /// <summary>Au-dela, l'instabilite est un fait d'archive et non un veto.
    ///
    /// **Non chiffré.** 45 jours couvrent large deux cycles de pilote GPU sans remonter a une
    /// configuration materielle qui n'existe plus.</summary>
    public static readonly TimeSpan FenetreInstabilite = TimeSpan.FromDays(45);

    /// <summary>La frequence est-elle si plate qu'un levier de frequence n'a rien a donner ?
    /// Rend le motif quand oui, <c>null</c> quand la question reste ouverte.</summary>
    private static string? FrequencePlate(DiagnosticMachine d)
    {
        // Sans lecture, on ne disqualifie pas : Unknown n'est pas une preuve d'inertie.
        if (d.FrequenceMedianeMhz is not { } mediane || mediane <= 0
            || d.FrequenceMinimaleMhz is not { } minimum)
            return null;

        // Un bridage publie change tout : la carte redescend pour une raison, et la lever est
        // exactement le sujet.
        if (d.Bridage is GpuLimiterObserved.PlafondPuissanceObserve
                      or GpuLimiterObserved.PlafondThermiqueObserve
                      or GpuLimiterObserved.ChuteDeFrequenceObservee)
            return null;

        var variation = (mediane - minimum) / mediane;
        return variation <= VariationFrequencePlate
            ? Docteur.T("CS.FrequencePlate", mediane, minimum)
            : null;
    }

    private static string MotifRetenu(DiagnosticMachine d, CandidateDescriptor c) => d.Goulot switch
    {
        BottleneckState.UnknownPartial => Docteur.T("CS.Retenu.Inconnu"),
        BottleneckState.CappedOrVsyncLimited => Docteur.T("CS.Retenu.Plafonne"),
        _ => Docteur.T("CS.Retenu.Coherent", MotifDuGoulot.Libelle(d.Goulot)),
    };
}
