namespace DrDxt.Measurement;

/// <summary>
/// Hygiène de la série d'images avant tout calcul statistique (compagnon de
/// <see cref="FrametimeStatistics"/>). Deux opérations, toutes deux pures, sans E/S :
///
///   • <see cref="Clean"/> retire les images qui ne sont pas du jeu : le chargement du début et les
///     secondes passées derrière une autre fenêtre (alt-tab). Publiées brutes, ces secondes passent
///     pour des défauts de la machine du joueur : un jeu plafonné à 30 fps hors focus pèse 33,3 ms par
///     image, et quelques secondes d'alt-tab suffisent à faire le pire centile d'une partie à 140 fps.
///
///   • <see cref="Slice"/> découpe une longue session en tranches de même durée. Plus une capture
///     dure, plus elle a de chances de croiser une image rare et lente, donc plus son pire centile
///     paraît mauvais, sans que la machine ait changé : deux sessions de durées très différentes
///     ne se comparent pas sur leurs centiles extrêmes. Le découpage donne la
///     dispersion propre de la machine d'une passe à l'autre, exactement l'entrée dont a besoin
///     <see cref="FrametimeStatistics.InterRunMarginOfError"/> : une seule longue session peut ainsi
///     produire une marge A/B honnête au lieu d'exiger trois passes de banc séparées.
///
/// <see cref="PresentMon.FrameCapture"/> ne porte aucun horodatage et n'en a pas besoin : les temps
/// d'image sont les écarts, le temps écoulé est leur somme cumulée.
/// </summary>
public static class FrameSeriesHygiene
{
    /// <summary>Borne haute des secondes écartées au début d'une capture (chargement du niveau,
    /// compilation des shaders), pas la quantité réellement retirée. Le vrai démarrage est mesuré par
    /// <see cref="DetectWarmup"/> ; cette valeur ne fait que borner la recherche.
    ///
    /// Mesuré sur une capture de 178 s prise en pleine partie (2026-09-01, i7-14700F / RTX 4060) : la
    /// seconde 0 est déjà à 7,84 ms pour une médiane de session de 7,27 ms, sans aucun démarrage.
    /// Retirer 90 s d'office jetterait la moitié des images (24 082 → 11 783) pour 0,9 % de changement
    /// du résultat, et laisserait 88 s, trop peu pour une seule tranche de 120 s : <see cref="Slice"/>
    /// ne produirait rien. Une capture qui commence en jeu n'a pas de démarrage ; seule une capture
    /// qui commence au lancement du jeu en a un.</summary>
    public const double DefaultWarmupSeconds = 90.0;

    /// <summary>Une seconde de début compte comme démarrage tant que son temps d'image médian dépasse
    /// ce multiple de la médiane de la session. Calibré sur la même série plutôt que sur une valeur
    /// fixe en millisecondes, pour valoir d'une machine à l'autre : ici, une seconde de chargement à
    /// 30 fps vaut ~4,6× une seconde de jeu à 150 fps, quand l'instabilité ordinaire du jeu reste près
    /// de 1,1×. Vérifié sur la capture ci-dessus : le démarrage détecté est de 0 s à 1,25×, 1,5× et 2×,
    /// le choix ne porte donc pas le résultat.</summary>
    public const double WarmupMedianFactor = 1.5;

    /// <summary>Une seconde entière dont le temps d'image médian dépasse ce seuil peut être hors jeu :
    /// le plafond hors focus (un jeu plafonné à 30 fps hors focus reste à 33,3 ms) ou un blocage de
    /// chargement. La médiane, pas le maximum : un seul à-coup légitime ne supprime jamais une bonne
    /// seconde. Bouton de calibrage : lire le plafond hors focus du jeu quand on peut le connaître
    /// (Call of Duty publie <c>MaxFpsOutOfFocus</c>) et placer ce seuil juste sous cette période, au
    /// lieu de deviner.
    ///
    /// Lent ne suffit pas : voir <see cref="CapRegularityMax"/>. Seul, ce seuil supprime 10 secondes
    /// d'un vrai à-coup à 25 fps (prouvé par un témoin positif).</summary>
    public const double DefaultOutOfFocusMedianMs = 25.0;

    /// <summary>
    /// Lent ne suffit pas pour supprimer une seconde : elle doit aussi être *régulière*.
    ///
    /// Une seconde n'est écartée que si son écart interquartile, rapporté à sa médiane, reste sous ce
    /// seuil. La raison est structurelle plutôt que statistique : un jeu hors focus est plafonné
    /// (le moteur dort jusqu'au tick suivant), sa période est donc régulière par construction. Un jeu
    /// qui peine vraiment est limité par un travail variable : ses temps d'image s'éparpillent. La
    /// médiane seule ne distingue pas un plafond à 30 fps d'un à-coup à 25 fps ; la régularité, si.
    ///
    /// La valeur s'appuie sur une mesure, pas sur un jugement. Sur les 2 170 secondes de vrai jeu
    /// gardées dans `tools/p3` (2026-09-01, i7-14700F / RTX 4060, ~143 fps), la seconde la plus
    /// régulière vaut 0,038 ; le 1er centile est 0,060 et la médiane 0,128. Le vrai jeu n'atteint
    /// jamais une telle régularité. Le seuil est posé à ce plancher observé : chaque doute se résout
    /// du côté sûr, garder les données. La règle du projet : un ralentissement soutenu est le résultat
    /// de la mesure, jamais un artefact à nettoyer.
    ///
    /// Plafond connu : un plafond dont l'instabilité du minuteur dépasse 4 % est gardé plutôt
    /// qu'écarté, et ces secondes comptent alors comme performance de la machine. C'est le sens
    /// voulu : l'erreur inverse présenterait une machine saine à un joueur qui a vu son jeu ramer.
    /// La vraie correction est d'observer l'état de la fenêtre pendant la capture ; d'ici là, c'est
    /// une inférence, et elle est nommée comme telle.
    /// </summary>
    public const double CapRegularityMax = 0.04;

    /// <summary>
    /// Résultat de <see cref="Clean"/>. Les comptes existent pour qu'un appelant voie ce qui a été
    /// retiré : un filtre qui mange une capture en silence est pire que pas de filtre.
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
    /// Retire d'une série d'images les secondes de démarrage et les secondes hors focus.
    ///
    /// Refuse de s'appliquer (rend l'entrée intacte avec <c>Applied = false</c>) quand le filtre
    /// garderait moins de <paramref name="minKeptFraction"/> des images. Cette garde est l'essentiel :
    /// sur une machine qui tourne vraiment à 30 fps, chaque seconde a une médiane au-dessus du seuil,
    /// et un filtre qui supprimerait toute la session changerait un vrai problème en résultat vide.
    /// Refuser est un troisième état : ni « filtré » ni « rien à filtrer ».
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
    /// Le meme nettoyage, rendu comme un masque, parce que sept series doivent bouger ensemble.
    ///
    /// `BenchmarkRunner` accumule `FrameTimesMs` et six series appariees par index
    /// (latence, GPU busy, GPU wait, CPU busy, present API, erreur d'animation). Le ratio de la
    /// frame i se lit a l'index i de chacune. Filtrer la premiere seule decale les six autres, et
    /// chaque ratio du classifieur devient faux sans lever la moindre erreur.
    ///
    /// <see cref="Clean"/> rend une serie, ce qui suffit a un rejeu hors ligne et pas a la
    /// production. Cette surcharge rend la decision, index par index, pour que l'appelant
    /// l'applique aux sept series d'un meme run, la ou elles sont encore coherentes entre elles.
    ///
    /// <paramref name="frameTimesMs"/> et le masque ont exactement la meme longueur : une frame
    /// invalide (nulle, negative, non finie) est marquee non gardee plutot que sautee, sinon les
    /// index ne voudraient plus rien dire.
    /// </summary>
    /// <param name="Garde">Un booleen par frame d'entree : la garder, ou non.</param>
    /// <param name="Applied">False quand le filtre a refuse : le masque est alors tout a vrai.</param>
    public sealed record MaskResult(
        IReadOnlyList<bool> Garde,
        int WarmupSecondsDropped,
        int OutOfFocusSecondsDropped,
        bool Applied);

    /// <inheritdoc cref="MaskResult"/>
    public static MaskResult Masque(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        double outOfFocusMedianMs = DefaultOutOfFocusMedianMs,
        double minKeptFraction = 0.20)
    {
        if (frameTimesMs is null || frameTimesMs.Count == 0)
            return new MaskResult(Array.Empty<bool>(), 0, 0, false);

        return CleanCore(frameTimesMs, warmupSeconds, outOfFocusMedianMs, minKeptFraction);
    }

    private static MaskResult CleanCore(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds,
        double outOfFocusMedianMs,
        double minKeptFraction)
    {
        var toutGarde = () => new MaskResult(
            Enumerable.Repeat(true, frameTimesMs.Count).ToList(), 0, 0, false);

        // Ranger les images par seconde entière. Les images invalides ne font rien avancer : elles ne
        // portent aucune durée fiable, elles sont donc retirées et ne décalent jamais l'horloge.
        // `secondeDe` garde la seconde de chaque index d'entree (−1 = frame invalide), parce
        // que le masque rendu doit avoir exactement la longueur de la serie d'entree : sans ca,
        // les index ne designeraient plus les memes frames dans les sept series.
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
        foreach (var (second, bucket) in bySecond)
        {
            if (second < warmupCutoff)
            {
                dropped.Add(second);
                warmupDropped++;
            }
            else if (Median(bucket) > outOfFocusMedianMs && EstUnPlafond(bucket))
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

        // La garde de dernier recours : sur une machine qui tourne genuinement au ralenti, tout
        // supprimer transformerait le pire diagnostic en resultat vide. Refuser est un troisieme
        // etat : ni « filtre », ni « rien a filtrer ».
        return gardees < valides * minKeptFraction
            ? toutGarde()
            : new MaskResult(garde, warmupDropped, focusDropped, true);
    }

    /// <summary>
    /// Découpe une série d'images en tranches consécutives de <paramref name="sliceSeconds"/> chacune.
    /// Un reste final plus court qu'une tranche est écarté : le garder comparerait une tranche courte
    /// à des tranches pleines, exactement le biais que le découpage doit retirer.
    /// Rend une liste vide quand la série est plus courte qu'une tranche (le dire ; jamais combler).
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
    /// La première seconde gardée après le démarrage (toute seconde antérieure est retirée), mesurée
    /// sur la session elle-même : une seconde compte comme démarrage tant que sa médiane dépasse
    /// <see cref="WarmupMedianFactor"/> × la médiane de toutes les médianes par seconde. Le balayage
    /// s'arrête à la première seconde normale (un à-coup en pleine partie est un défaut à signaler,
    /// jamais un démarrage à cacher) et ne dépasse jamais <paramref name="cap"/>.
    ///
    /// Plafond connu : la médiane de référence inclut les secondes de démarrage ; une capture à plus
    /// de moitié de démarrage tire la référence vers le haut et sous-détecte. C'est le sens sûr (on
    /// garde des images de chargement plutôt que de supprimer du jeu), et une capture surtout faite
    /// d'écran de chargement n'est de toute façon pas un banc.
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

    /// <summary>Cette seconde lente est-elle un plafond (régulier par construction) plutôt qu'un jeu
    /// qui peine ? Voir <see cref="CapRegularityMax"/> pour la raison du critère de régularité et
    /// l'origine du 0,04. Trop peu d'images pour juger (moins de quatre) veut dire aucun verdict, et
    /// aucun verdict veut dire garder : supprimer sur une supposition est l'erreur qui cache un vrai ralentissement.</summary>
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
