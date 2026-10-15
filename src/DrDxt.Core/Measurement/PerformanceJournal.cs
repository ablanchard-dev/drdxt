using static DrDxt.Core.TexteFrancais;
namespace DrDxt.Core;

/// <summary>
/// Une mesure conservee entre deux sessions. Le strict necessaire pour repondre a « mon PC est-il
/// plus lent qu'avant ? », et rien de plus : un journal qui enregistre tout finit par etre efface.
/// </summary>
/// <param name="TakenUtc">Quand. C'est la date qui ordonne le journal, jamais sa position dans le
/// fichier : un fichier append-only peut etre relu en desordre apres une reprise.</param>
/// <param name="MachineId">Deux machines ne se comparent jamais. Un disque deplace, un
/// dual-boot, un PC remplace, et le produit annoncerait « ta machine a perdu 40 % ».</param>
/// <param name="AvgFps">Ce que la session a rendu.</param>
/// <param name="MarginOfError">Ce que la session pouvait resoudre. <c>+∞</c> sous trois passes,
/// et une comparaison sans resolution ne prouve rien, jamais.</param>
/// <param name="Runs">Le nombre de passes, garde pour relire une entree ancienne sans deviner.</param>
/// <param name="KeptFraction">L'exposition : la part de trames que l'hygiene a gardees. Deux
/// mesures qui n'ont pas vu la meme chose ne se comparent pas, exactement comme dans un A/B.</param>
/// <param name="GameId">quel jeu a ete mesure. Aussi decisif que la machine : le produit mesure le
/// jeu qui tourne, quel qu'il soit, et comparer Warzone a un autre titre annoncerait une
/// « degradation » qui n'est qu'un changement de jeu.
/// <c>null</c> = jeu non identifie (entree ecrite avant ce champ) : on ne compare pas.
/// Optionnel et en queue : toutes les entrees deja ecrites restent lisibles.</param>
/// <param name="Scene">ou la mesure a ete prise, telle que le joueur l'a declaree avant le scan.
/// L'ecran de scan demande si l'on joue une partie, un stand de tir ou un menu, et la
/// reponse voyage avec la mesure. Un menu rend bien plus
/// d'images qu'une partie : le comparer a une partie fabrique une « baisse ». Meme regle que la
/// machine et le jeu : on ne compare qu'a scene egale. <c>null</c> (entree ecrite avant ce
/// champ) vaut « non declaree ».</param>
public sealed record PerformanceSample(
    DateTimeOffset TakenUtc,
    string MachineId,
    double AvgFps,
    double MarginOfError,
    int Runs,
    double? KeptFraction,
    string? GameId = null,
    SceneRepresentativity? Scene = null);

/// <summary>
/// La derive entre deux sessions : la question qu'aucun scan unique ne peut poser.
///
/// Un seul fichier survivait a une session de DrDXT : `memory-baseline.json`. Ni les mesures, ni
/// les verdicts, ni les resolutions. Le produit redecouvrait la machine a chaque lancement, et une
/// **degradation lente** est precisement ce qu'un scan isole ne voit pas : chaque mesure prise
/// seule parait normale.
///
/// Ce journal ne conserve pas des donnees pour le principe. Il compare la plus ancienne mesure
/// assez precise (l'etalon, voir <see cref="Reference(IReadOnlyList{PerformanceSample})"/>)
/// a la plus recente, sur la meme machine et le meme jeu, **avec la resolution des deux**, par le meme
/// <see cref="Evidence"/> que l'A/B. Une derive sous le bruit reste `NoResolvedChange`, jamais une
/// alerte : un produit qui crie a la degradation tous les mois n'est plus lu.
///
/// Pur, aucune I/O : le fichier vit ailleurs, cette logique se teste sans disque.
/// </summary>
public static class PerformanceJournal
{
    /// <summary>Ce que le journal etablit entre la premiere mesure et la derniere.
    ///
    /// Rend un rapport sans <see cref="PerformanceEvidence.Game"/> quand il n'y a rien a comparer :
    /// journal vide, une seule mesure, ou deux machines differentes. Il faut le dire : rendre un
    /// zero rassurant serait affirmer une stabilite que personne n'a mesuree.</summary>
    public static PerformanceEvidence Derive(IReadOnlyList<PerformanceSample> journal)
    {
        if (Comparables(journal) is not { } comparables)
            return new PerformanceEvidence(null, null);

        var derniere = comparables[^1];
        var reference = Reference(comparables);

        if (!(reference.AvgFps > 0) || !double.IsFinite(derniere.AvgFps))
            return new PerformanceEvidence(null,
                new GameEvidence(Metrique, Evidence.Invalide(Docteur.T("PJ.ReferenceInexploitable"))));

        // Meme regle que l'A/B : deux mesures qui n'ont pas vu la meme chose ne se comparent pas.
        if (reference.KeptFraction is { } a && derniere.KeptFraction is { } b
            && Math.Abs(a - b) > ExpositionComparableMax)
            return new PerformanceEvidence(null,
                new GameEvidence(Metrique, Evidence.Invalide(
                    Docteur.T("PJ.Exposition", a, b))));

        var deltaPct = (derniere.AvgFps - reference.AvgFps) / reference.AvgFps * 100.0;
        var resolutionPct = (reference.MarginOfError + derniere.MarginOfError) / reference.AvgFps * 100.0;

        return new PerformanceEvidence(null,
            new GameEvidence(Metrique, Evidence.From(deltaPct, resolutionPct)));
    }

    /// <summary>Borne la taille du journal en gardant une reference par jeu.
    ///
    /// Tronquer par la fin detruirait la mesure la plus ancienne, c'est-a-dire tout l'interet :
    /// un journal de trente jours glissants ne voit jamais une degradation lente, puisque chaque
    /// comparaison se fait contre un passe deja degrade.
    ///
    /// Et une seule reference ne suffit pas. La comparaison se fait par (machine, jeu) : garder
    /// la plus ancienne mesure du fichier conservait l'etalon d'un seul jeu et detruisait celui de
    /// tous les autres : la derive de ces jeux se serait alors calculee contre un passe deja
    /// degrade, sans que rien ne le signale. Exactement le defaut que cet elagage existe pour
    /// eviter, deplace d'un cran.
    ///
    /// Si les references a elles seules depassent le plafond, ce sont les plus recentes qui
    /// restent : un jeu auquel personne n'a touche depuis deux cents mesures cede sa place a celui
    /// qu'on joue. Perdre une reference se paie, mais depasser le plafond se paierait a chaque
    /// lecture.</summary>
    public static IReadOnlyList<PerformanceSample> Elaguer(
        IReadOnlyList<PerformanceSample> journal, int maximum)
    {
        if (journal is null || maximum < 2 || journal.Count <= maximum)
            return journal ?? Array.Empty<PerformanceSample>();

        var trie = journal.OrderBy(e => e.TakenUtc).ToList();

        // Des indices, pas des entrees : deux mesures identiques a la valeur pres existent (meme
        // machine, meme jeu, meme FPS) et un ensemble d'enregistrements les confondrait.
        // Et l'etalon garde est celui que le juge prend (voir Reference) : garder la plus ancienne
        // mesure alors que le juge en prend une autre reviendrait a elaguer l'etalon reel.
        var garde = new SortedSet<int>();
        foreach (var groupe in trie.Select((e, i) => (e, i))
                     .GroupBy(x => x.e.MachineId + "|" + (x.e.GameId ?? string.Empty) + "|" + SceneDe(x.e),
                              StringComparer.OrdinalIgnoreCase))
        {
            var liste = groupe.ToList();
            var etalon = Reference(liste.Select(x => x.e).ToList());
            garde.Add(liste.First(x => ReferenceEquals(x.e, etalon)).i);
        }

        while (garde.Count > maximum) garde.Remove(garde.Min);

        for (var i = trie.Count - 1; i >= 0 && garde.Count < maximum; i--) garde.Add(i);

        return garde.Select(i => trie[i]).ToList();
    }

    /// <summary>L'etalon : la mesure contre laquelle la derniere se compare.
    ///
    /// Prendre toujours la plus ancienne laisserait une seule premiere mesure bruitee condamner le
    /// journal. Mesure reelle : 164 ± 28 le 11/09 (17 %, compilations en fond), puis 136 ± 5 (3,7 %)
    /// mesure en jeu le 16/09. Avec la premiere pour etalon, chaque comparaison heriterait de ses ±17 % :
    /// une baisse de 15 % ne serait jamais resolue.
    ///
    /// L'etalon est donc la plus ancienne mesure assez precise pour servir ; a defaut, la plus
    /// ancienne tout court (le journal ne se tait pas parce que tout est bruite : il dit sa
    /// resolution). Le journal passe trie par date et deja filtre sur (machine, jeu).</summary>
    public static PerformanceSample Reference(IReadOnlyList<PerformanceSample> comparablesTries) =>
        comparablesTries.FirstOrDefault(e => e.AvgFps > 0
                                             && e.MarginOfError / e.AvgFps * 100.0 <= MargeDeReferenceMaxPct)
        ?? comparablesTries[0];

    /// <summary>L'etalon du journal tel que <see cref="Derive"/> le prend : pour la derniere
    /// mesure, sur sa machine et son jeu. <c>null</c> quand il n'y a rien a comparer.</summary>
    public static PerformanceSample? Etalon(IReadOnlyList<PerformanceSample> journal) =>
        Comparables(journal) is { } comparables ? Reference(comparables) : null;

    /// <summary>Les mesures de la machine et du jeu de la derniere mesure, triees par date ;
    /// <c>null</c> quand il n'y a rien a comparer (journal vide, une seule mesure, jeu non
    /// identifie, ou une seule mesure de ce jeu sur cette machine).</summary>
    private static List<PerformanceSample>? Comparables(IReadOnlyList<PerformanceSample> journal)
    {
        if (journal is null || journal.Count < 2) return null;

        // La date ordonne, jamais la position : un fichier append-only relu apres une reprise peut
        // arriver en desordre, et comparer la mauvaise paire donnerait une derive inventee.
        var trie = journal.OrderBy(e => e.TakenUtc).ToList();
        var derniere = trie[^1];

        // Le jeu est aussi decisif que la machine, et il manquait. Le produit mesure le jeu qui
        // tourne, quel qu'il soit : un joueur qui scanne pendant Warzone puis pendant un autre
        // titre remplit le journal de deux echelles de FPS sans rapport, et la comparaison
        // annoncerait une « degradation » qui n'est qu'un changement de jeu : la meme faute que
        // comparer deux PC, en moins visible.
        //
        // Un jeu non identifie ne se rapproche de rien : « inconnu » n'est pas « le meme ».
        if (string.IsNullOrWhiteSpace(derniere.GameId)) return null;

        // On compare la derniere mesure a l'etalon du meme jeu sur la meme machine. Filtrer
        // plutot que refuser : une seule mesure d'un autre titre ne doit pas rendre le journal muet
        // pour toujours.
        // Et a scene egale : une partie ne se compare pas a un menu, ni a un stand de tir.
        var comparables = trie
            .Where(e => string.Equals(e.MachineId, derniere.MachineId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(e.GameId, derniere.GameId, StringComparison.OrdinalIgnoreCase)
                     && SceneDe(e) == SceneDe(derniere))
            .ToList();

        // Mieux vaut se taire que d'annoncer une chute qui n'est qu'un changement de PC, de jeu ou de scene.
        return comparables.Count < 2 ? null : comparables;
    }

    /// <summary>Marge relative (marge / moyenne, en pourcent) au-dela de laquelle une mesure ne
    /// peut pas servir d'etalon. Chiffree sur les mesures reelles : 17 % pour la mesure polluee
    /// du 11/09/2026, 3,7 % pour la mesure en jeu du 16/09, ±2,25 FPS sur 5 passes pour les
    /// captures de `tools/p3`. Reversible : une constante.</summary>
    public const double MargeDeReferenceMaxPct = 10.0;

    /// <summary>La scene d'une entree, les entrees anterieures au champ valant « non declaree ».</summary>
    private static SceneRepresentativity SceneDe(PerformanceSample e) => e.Scene ?? SceneRepresentativity.Unknown;

    /// <summary>La grandeur suivie. Une seule pour l'instant, et c'est deliberé : ajouter les
    /// 1 % lows demande de decider ce que « la machine a ralenti » veut dire quand les deux
    /// grandeurs divergent, et cette question n'est pas tranchee.</summary>
    public const string Metrique = "fps.moyen";

    /// <summary>Ecart d'exposition au-dela duquel deux mesures ne sont plus comparables. Meme
    /// valeur et meme raison que <c>AbComparer.ExpositionComparableMax</c> : **non chiffré**,
    /// encadre par la mesure du 07/09/2026 : sur 2 170 secondes de jeu reel, l'hygiene retire 0 %,
    /// donc deux mesures propres sont a zero d'ecart.</summary>
    public const double ExpositionComparableMax = 0.10;
}
