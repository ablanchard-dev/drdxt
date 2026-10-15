using System.Diagnostics;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Le banc memoire : debit, latence, et l'escalier de cache.
///
/// Lecture pure. Il alloue de la memoire et la lit. Il n'ecrit rien, ne demande aucune elevation,
/// ne touche ni au BIOS ni au registre.
///
/// Mesure le 06/09/2026 sur la machine de reference (`tools/memprobe`, qui n'est plus qu'un appelant) :
/// sigma/moyenne de 1,83 % sur le debit et 3,45 % sur la latence, contre un plancher de bruit
/// in-game de 9,9 %.
///
/// Le piege : une chaine de 16 Mo tient dans le L3 de 33 Mo d'un i7-14700F et rend 25 ns, la
/// latence du cache prise pour celle de la DRAM. Le controle Q-04 l'attrape.
/// </summary>
public static class MemoryBench
{
    /// <summary>32 M de longs = 256 Mo, environ 8x le plus gros L3 grand public. Le buffer doit
    /// deborder largement du cache, sinon on mesure le processeur et pas la memoire.</summary>
    private const int BandwidthElements = 32 * 1024 * 1024;

    /// <summary>Un acces par ligne de cache (64 octets = 8 longs) : chaque lecture tire une ligne
    /// entiere depuis la DRAM, et rien n'est lu deux fois.</summary>
    private const int Stride = 8;

    /// <summary>Les fenetres de l'escalier, en octets. Elles encadrent les tailles usuelles :
    /// L1 (dizaines de Ko), L2 (1-2 Mo), L3 (dizaines de Mo), puis franchement au-dela.</summary>
    private static readonly long[] StaircaseWindows =
    [
        32 * 1024L, 48 * 1024L, 256 * 1024L, 2 * 1024 * 1024L,
        16 * 1024 * 1024L, 32 * 1024 * 1024L, 128 * 1024 * 1024L,
    ];

    /// <summary>
    /// Mesure la memoire. <paramref name="passes"/> passes pour le debit et la latence, dont on
    /// tire la moyenne et la dispersion : sans la dispersion, aucun A/B ne peut distinguer un gain
    /// du bruit. L'escalier, lui, se parcourt une fois : c'est sa forme qui compte, pas sa
    /// precision au pourcent.
    /// </summary>
    public static MemoryMeasurement Measure(int passes = 10)
    {
        // Chauffe : la premiere passe paie l'allocation et les defauts de page. La compter
        // fausserait la moyenne et gonflerait l'ecart-type.
        BandwidthGBs(1);
        LatencyNs(BandwidthElements, 1_000_000);

        var bw = new double[passes];
        for (int i = 0; i < passes; i++) bw[i] = BandwidthGBs(3);

        var lat = new double[passes];
        for (int i = 0; i < passes; i++) lat[i] = LatencyNs(BandwidthElements, 3_000_000);

        var staircase = StaircaseWindows
            .Select(w => new StaircasePoint(w, LatencyNs((int)(w / sizeof(int)), 2_000_000)))
            .ToList();

        return new MemoryMeasurement(
            BandwidthGBs: bw.Average(),
            BandwidthSigmaPct: SigmaPct(bw),
            LatencyNs: lat.Average(),
            LatencySigmaPct: SigmaPct(lat),
            Staircase: staircase);
    }

    private static double SigmaPct(double[] v)
    {
        double m = v.Average();
        if (m <= 0) return 0;
        double sd = Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Length);
        return 100.0 * sd / m;
    }

    private static double BandwidthGBs(int rounds)
    {
        var a = new long[BandwidthElements];
        for (int i = 0; i < BandwidthElements; i++) a[i] = i;

        long sink = 0;
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < rounds; r++)
            for (int i = 0; i < BandwidthElements; i += Stride) sink += a[i];
        sw.Stop();

        // Empeche le JIT de supprimer la boucle : la somme doit servir a quelque chose.
        if (sink == long.MinValue) Console.Write(' ');

        double bytes = (double)(BandwidthElements / Stride) * 64.0 * rounds;
        return bytes / sw.Elapsed.TotalSeconds / 1e9;
    }

    /// <summary>
    /// Latence d'un acces aleatoire dans une zone de <paramref name="elements"/> entiers. Chaine
    /// de pointeurs : chaque acces depend du precedent, donc le prefetcheur ne peut rien anticiper
    /// et la latence ne peut pas etre masquee.
    /// </summary>
    private static double LatencyNs(int elements, int steps)
    {
        var chain = ChaineUnique(elements);

        int q = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) q = chain[q];
        sw.Stop();

        if (q == int.MinValue) Console.Write(' ');
        return sw.Elapsed.TotalMilliseconds * 1e6 / steps;
    }

    /// <summary>
    /// Une permutation aleatoire, chainee en un seul cycle qui passe par chaque element : partant de
    /// 0, on parcourt toute la zone avant de revenir.
    ///
    /// Une chaine construite depuis 0 en suivant simplement la permutation reviendrait au depart en
    /// arrivant sur la valeur 0 et reecrirait le premier maillon : le cycle parcouru ne couvrirait
    /// qu'une fraction aleatoire de la zone (3,1 % des 128 Mo, 23,7 % des 2 Mo avec la graine du
    /// banc). L'effet mesure est faible, parce que les elements visites restent eparpilles sur toute
    /// la zone (au plus 6,5 %, sur la fenetre de 16 Mo), mais chaque fenetre ne mesurerait pas la
    /// zone qu'elle annonce.
    /// </summary>
    internal static int[] ChaineUnique(int elements)
    {
        var next = new int[elements];
        for (int i = 0; i < elements; i++) next[i] = i;

        // Graine fixe : deux executions parcourent exactement le meme chemin, sinon un A/B
        // comparerait deux permutations differentes en croyant comparer deux reglages.
        var rng = new Random(12345);
        for (int i = elements - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (next[i], next[j]) = (next[j], next[i]);
        }

        var chain = new int[elements];
        for (int i = 0; i < elements; i++) chain[next[i]] = next[(i + 1) % elements];
        return chain;
    }
}
