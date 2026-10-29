namespace DrDxt.Measurement;

/// <summary>
/// Hygiène de la série d'images avant tout calcul statistique, pure, sans E/S :
///   • <see cref="Clean"/> retire le chargement du début et les secondes hors focus (alt-tab) : un jeu plafonné à 30 fps
///     hors focus pèse 33,3 ms par image, et quelques secondes suffisent à faire le pire centile d'une partie à 140 fps.
///   • <see cref="Slice"/> découpe une session en tranches de même durée : plus une capture dure, plus son pire centile
///     paraît mauvais. Les tranches donnent la dispersion d'une passe à l'autre qu'attend
///     <see cref="FrametimeStatistics.InterRunMarginOfError"/>, depuis une seule longue session.
/// Le temps écoulé est la somme cumulée des temps d'image : <see cref="PresentMon.FrameCapture"/> n'a pas d'horodatage.
/// </summary>
public static class FrameSeriesHygiene
{
    /// <summary>Borne haute de la recherche du démarrage (chargement, shaders), pas la quantité retirée : celle-ci est
    /// mesurée par <see cref="DetectWarmup"/>. Capture de 178 s en pleine partie (2026-09-01, i7-14700F / RTX 4060) :
    /// seconde 0 à 7,84 ms pour une médiane de 7,27 ms. Retirer 90 s d'office jetterait 24 082 → 11 783 images pour 0,9 %
    /// de changement, et 88 s restantes ne feraient pas une tranche de 120 s.</summary>
    public const double DefaultWarmupSeconds = 90.0;

    /// <summary>Une seconde de début compte comme démarrage tant que sa médiane dépasse ce multiple de celle de la session
    /// (relatif, pour valoir d'une machine à l'autre) : un chargement à 30 fps vaut ~4,6× une seconde à 150 fps, le jeu
    /// ordinaire ~1,1×. Sur la capture ci-dessus, 0 s détectée à 1,25×, 1,5× et 2× : le choix ne porte pas le
    /// résultat.</summary>
    public const double WarmupMedianFactor = 1.5;

    /// <summary>Une seconde dont la médiane dépasse ce seuil peut être hors jeu (plafond hors focus à 33,3 ms, blocage de
    /// chargement). La médiane, pas le maximum : un à-coup légitime ne supprime jamais une bonne seconde. Bouton de
    /// calibrage : lire le plafond hors focus du jeu (Call of Duty publie <c>MaxFpsOutOfFocus</c>) et poser ce seuil
    /// juste sous sa période. Lent ne suffit pas (<see cref="CapRegularityMax"/>) : seul, ce seuil supprime 10 s d'un
    /// vrai à-coup à 25 fps (témoin positif).</summary>
    public const double DefaultOutOfFocusMedianMs = 25.0;

    /// <summary>
    /// Une seconde lente n'est écartée que si elle est aussi régulière : écart interquartile / médiane sous ce seuil. Un
    /// jeu hors focus est plafonné (le moteur dort jusqu'au tick), donc régulier ; un jeu qui peine s'éparpille.
    /// Mesure : sur 2 170 s de vrai jeu dans `tools/p3` (2026-09-01, i7-14700F / RTX 4060, ~143 fps), la seconde la plus
    /// régulière vaut 0,038, le 1er centile 0,060, la médiane 0,128 ; le seuil est posé à ce plancher, le doute garde les
    /// données (un ralentissement soutenu est la mesure, jamais un artefact). Plafond connu : un minuteur instable à plus
    /// de 4 % est gardé, l'erreur sûre. Un V-Sync qui tombe de 60 à 30 i/s a la même signature : en production, les
    /// secondes écartées sont bornées au temps vu hors premier plan (<c>horsFocusMaxSecondes</c> de <see cref="Masque"/>).
    /// </summary>
    public const double CapRegularityMax = 0.04;

    /// <summary>
    /// Résultat de <see cref="Clean"/>. Les comptes montrent ce qui a été retiré : un filtre qui mange une capture en
    /// silence est pire que pas de filtre.
    /// </summary>
    /// <param name="FrameTimesMs">Les images gardées, dans l'ordre.</param>
    /// <param name="WarmupSecondsDropped">Secondes entières retirées comme démarrage.</param>
    /// <param name="OutOfFocusSecondsDropped">Secondes entières retirées comme hors focus / bloquées.</param>
    /// <param name="Applied">False quand le filtre a refusé de tourner : voir <see cref="Clean"/>.</param>
    public sealed record CleanResult(
        IReadOnlyList<double> FrameTimesMs,
        int WarmupSecondsDropped,
        int OutOfFocusSecondsDropped,
        bool Applied);

    /// <summary>
    /// Retire les secondes de démarrage et hors focus. Refuse (entrée intacte, <c>Applied = false</c>) sous
    /// <paramref name="minKeptFraction"/> des images gardées : à 30 fps réels, chaque seconde dépasse le seuil, et tout
    /// supprimer changerait un vrai problème en résultat vide. Refuser est un troisième état.
    /// </summary>
    public static CleanResult Clean(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        double outOfFocusMedianMs = DefaultOutOfFocusMedianMs,
        double minKeptFraction = 0.20)
    {
        var masque = Masque(frameTimesMs, warmupSeconds, outOfFocusMedianMs, minKeptFraction);
        if (!masque.Applied)
            return new CleanResult(frameTimesMs ?? Array.Empty<double>(), 0, 0, false);

        var gardees = new List<double>(frameTimesMs!.Count);
        for (var i = 0; i < frameTimesMs.Count; i++)
            if (masque.Garde[i]) gardees.Add(frameTimesMs[i]);

        return new CleanResult(gardees, masque.WarmupSecondsDropped, masque.OutOfFocusSecondsDropped, true);
    }

    /// <summary>
    /// Le meme nettoyage rendu comme un masque : `BenchmarkRunner` accumule `FrameTimesMs` et six series appariees par
    /// index (latence, GPU busy, GPU wait, CPU busy, present API, erreur d'animation). Filtrer la premiere seule
    /// fausserait chaque ratio du classifieur sans erreur ; l'appelant applique le masque aux sept. Meme longueur que
    /// <paramref name="frameTimesMs"/> : une frame invalide est marquee non gardee, jamais sautee.
    /// </summary>
    /// <param name="Garde">Un booleen par frame d'entree : la garder, ou non.</param>
    /// <param name="Applied">False quand le filtre a refuse : le masque est alors tout a vrai.</param>
    /// <param name="Retenues">Les images valides que le filtre garderait, applique ou refuse : l'exposition, car un refus
    /// garde la serie entiere d'une passe vecue surtout hors du jeu.</param>
    public sealed record MaskResult(
        IReadOnlyList<bool> Garde,
        int WarmupSecondsDropped,
        int OutOfFocusSecondsDropped,
        bool Applied,
        int Retenues = 0);

    /// <inheritdoc cref="MaskResult"/>
    /// <param name="horsFocusMaxSecondes">Le temps que la sonde de premier plan a vu le jeu derrière une autre fenêtre :
    /// au-delà, une seconde lente et régulière est gardée (un V-Sync de 60 à 30 i/s a la même signature, et ses chutes
    /// sont la mesure). Sans borne : l'inférence seule (rejeu hors ligne).</param>
    public static MaskResult Masque(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        double outOfFocusMedianMs = DefaultOutOfFocusMedianMs,
        double minKeptFraction = 0.20,
        double horsFocusMaxSecondes = double.PositiveInfinity)
    {
        if (frameTimesMs is null || frameTimesMs.Count == 0)
            return new MaskResult(Array.Empty<bool>(), 0, 0, false);

        return CleanCore(frameTimesMs, warmupSeconds, outOfFocusMedianMs, minKeptFraction, horsFocusMaxSecondes);
    }

    private static MaskResult CleanCore(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds,
        double outOfFocusMedianMs,
        double minKeptFraction,
        double horsFocusMaxSecondes = double.PositiveInfinity)
    {
        var toutGarde = () => new MaskResult(
            Enumerable.Repeat(true, frameTimesMs.Count).ToList(), 0, 0, false);

        // Les images par seconde entière ; une image invalide ne fait pas avancer l'horloge. `secondeDe` garde la seconde
        // de chaque index (−1 = invalide) : le masque doit avoir la longueur de l'entrée.
        var bySecond = new Dictionary<int, List<double>>();
        var secondeDe = new int[frameTimesMs.Count];
        var valides = 0;
        var elapsedMs = 0.0;
        for (var i = 0; i < frameTimesMs.Count; i++)
        {
            var ms = frameTimesMs[i];
            if (!(ms > 0) || !double.IsFinite(ms)) { secondeDe[i] = -1; continue; }
            var second = (int)(elapsedMs / 1000.0);
            elapsedMs += ms;
            if (!bySecond.TryGetValue(second, out var bucket))
                bySecond[second] = bucket = new List<double>();
            bucket.Add(ms);
            secondeDe[i] = second;
            valides++;
        }

        if (valides == 0)
            return toutGarde();

        var warmupCutoff = DetectWarmup(bySecond, (int)Math.Max(0, warmupSeconds));
        var warmupDropped = 0;
        var focusDropped = 0;
        var dropped = new HashSet<int>();
        foreach (var (second, bucket) in bySecond.OrderBy(kv => kv.Key))
        {
            if (second < warmupCutoff)
            {
                dropped.Add(second);
                warmupDropped++;
            }
            else if (focusDropped < horsFocusMaxSecondes && Median(bucket) > outOfFocusMedianMs && EstUnPlafond(bucket))
            {
                dropped.Add(second);
                focusDropped++;
            }
        }

        var garde = new bool[frameTimesMs.Count];
        var gardees = 0;
        for (var i = 0; i < frameTimesMs.Count; i++)
        {
            // Une frame invalide n'est jamais gardee : elle ne porte aucune duree exploitable.
            garde[i] = secondeDe[i] >= 0 && !dropped.Contains(secondeDe[i]);
            if (garde[i]) gardees++;
        }

        // Dernier recours : sur une machine vraiment lente, tout supprimer viderait le pire diagnostic. On refuse.
        return gardees < valides * minKeptFraction
            ? toutGarde() with { Retenues = gardees }
            : new MaskResult(garde, warmupDropped, focusDropped, true, gardees);
    }

    /// <summary>
    /// Découpe une série d'images en tranches consécutives de <paramref name="sliceSeconds"/> chacune.
    /// Le reste final, plus court, est écarté : le garder ramènerait le biais de durée. Série plus courte qu'une tranche :
    /// liste vide, jamais comblée.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<double>> Slice(
        IReadOnlyList<double> frameTimesMs, double sliceSeconds)
    {
        var slices = new List<IReadOnlyList<double>>();
        if (frameTimesMs is null || frameTimesMs.Count == 0 || !(sliceSeconds > 0))
            return slices;

        var sliceMs = sliceSeconds * 1000.0;
        var current = new List<double>();
        var inSlice = 0.0;
        foreach (var ms in frameTimesMs)
        {
            if (!(ms > 0) || !double.IsFinite(ms)) continue;
            current.Add(ms);
            inSlice += ms;
            if (inSlice < sliceMs) continue;
            slices.Add(current);
            current = new List<double>();
            inSlice = 0.0;
        }

        return slices; // `current` est le reste court : écarté exprès.
    }

    /// <summary>
    /// La première seconde gardée après le démarrage : une seconde est démarrage tant que sa médiane dépasse
    /// <see cref="WarmupMedianFactor"/> × la médiane des médianes par seconde. Arrêt à la première seconde normale (un
    /// à-coup en pleine partie est un défaut à signaler), jamais au-delà de <paramref name="cap"/>. Plafond connu : une
    /// capture à plus de moitié de chargement sous-détecte, le sens sûr.
    /// </summary>
    internal static int DetectWarmup(Dictionary<int, List<double>> bySecond, int cap)
    {
        if (cap <= 0 || bySecond.Count == 0) return 0;

        var seconds = bySecond.Keys.OrderBy(s => s).ToList();
        var medians = seconds.Select(s => Median(bySecond[s])).ToList();
        var reference = Median(medians.ToList());
        if (!(reference > 0)) return 0;

        // La coupure et le plafond se comptent en secondes réelles, pas en rangs de seau : une image de plusieurs
        // secondes fait sauter des numéros, et le rang 2 peut être la seconde 3.
        var limit = reference * WarmupMedianFactor;
        var dropped = 0;
        while (dropped < medians.Count && seconds[dropped] < cap && medians[dropped] > limit) dropped++;
        return dropped < seconds.Count ? seconds[dropped] : seconds[^1] + 1;
    }

    /// <summary>Cette seconde lente est-elle un plafond plutôt qu'un jeu qui peine (<see cref="CapRegularityMax"/>) ?
    /// Moins de quatre images : aucun verdict, donc garder.</summary>
    private static bool EstUnPlafond(List<double> bucket)
    {
        if (bucket.Count < 4) return false;

        var sorted = bucket.OrderBy(x => x).ToList();
        var mediane = Median(sorted);
        if (!(mediane > 0)) return false;

        var q1 = sorted[sorted.Count / 4];
        var q3 = sorted[3 * sorted.Count / 4];
        return (q3 - q1) / mediane < CapRegularityMax;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
