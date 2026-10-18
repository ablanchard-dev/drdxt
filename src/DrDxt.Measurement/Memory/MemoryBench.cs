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
        var zone = ElementsLatence(OctetsL3());
        // Chauffe : la premiere passe paie l'allocation et les defauts de page. La compter
        // fausserait la moyenne et gonflerait l'ecart-type.
        BandwidthGBs(1);
        LatencyNs(zone, 1_000_000);

        var bw = new double[passes];
        for (int i = 0; i < passes; i++) bw[i] = BandwidthGBs(3);
        // Plusieurs fils, la mediane de trois : c'est elle qui peut dire « un seul canal ». Le nombre de fils est borne par la
        // memoire libre ; 0 = non mesure.
        var multi = BandwidthMultiFilGBs(FilsDuDebit(Environment.ProcessorCount, CommitPressureReader.LibrePhysique()));

        // La latence et l'escalier sur un seul cœur, puis l'affinité d'origine rendue.
        var (lat, staircase) = SurUnSeulCoeur.Executer(() =>
        {
            var l = new double[passes];
            for (int i = 0; i < passes; i++) l[i] = LatencyNs(zone, 3_000_000);
            return (l, StaircaseWindows
                .Select(w => new StaircasePoint(w, LatencyNs((int)(w / sizeof(int)), 2_000_000)))
                .ToList());
        });

        return new MemoryMeasurement(
            BandwidthGBs: bw.Average(),
            BandwidthSigmaPct: SigmaPct(bw),
            LatencyNs: lat.Average(),
            LatencySigmaPct: SigmaPct(lat),
            Staircase: staircase,
            BandwidthMultiFilGBs: multi);
    }

    /// <summary>La zone de la latence « DRAM », en entiers : au moins 128 Mio, et au moins quatre fois le L3 lu. 128 Mio
    /// fixes laissaient une part des accès dans le cache d'un X3D (96 Mo de L3, 128 Mo sur un 9950X3D) : la latence
    /// de la mémoire était sous-estimée. Quatre fois, pour que la part encore servie par le L3 reste sous 25 %.</summary>
    internal static int ElementsLatence(long octetsL3)
        => (int)Math.Min(int.MaxValue / 2, Math.Max(128L * 1024 * 1024, 4 * octetsL3) / sizeof(int));

    /// <summary>La somme des caches L3 de la machine (un par CCD sur un Ryzen à deux CCD), sur tous les groupes de
    /// processeurs (<see cref="DrDxt.Core.CachesL3"/>). 0 si illisible.</summary>
    internal static long OctetsL3() => DrDxt.Core.CachesL3.Lire()?.Sum(c => c.Octets) ?? 0;

    private static double SigmaPct(double[] v)
    {
        double m = v.Average();
        if (m <= 0) return 0;
        double sd = Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Length);
        return 100.0 * sd / m;
    }

    /// <summary>Le debit de lecture de <paramref name="fils"/> fils a la fois, chacun sur son tampon de 64 Mio, une lecture
    /// par ligne de 64 octets. Mesure le 17/10/2026 sur la machine de reference (DDR4-3800, deux canaux, plafond 60,8 Go/s) :
    /// 39,6 Go/s sur un fil, 50 a 53 Go/s de 2 a 28 fils.
    /// La mediane de trois series, sur les memes tampons : alloues une fois, ils ne s'empilent pas d'une serie a l'autre.
    /// 0 sans fil (memoire libre trop basse ou non lue).</summary>
    internal static double BandwidthMultiFilGBs(int fils)
    {
        if (fils <= 0) return 0;
        const int Elements = (int)(TamponDuDebit / sizeof(long));
        var tampons = Enumerable.Range(0, fils).Select(_ => { var a = new long[Elements]; for (int i = 0; i < a.Length; i++) a[i] = i; return a; }).ToArray();
        var sommes = new long[fils];
        double Serie()
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, fils, new ParallelOptions { MaxDegreeOfParallelism = fils }, f =>
            {
                long s = 0;
                var a = tampons[f];
                for (int r = 0; r < 3; r++)
                    for (int i = 0; i < a.Length; i += Stride) s += a[i];
                sommes[f] = s;
            });
            sw.Stop();
            return fils * (double)(Elements / Stride) * 64.0 * 3 / sw.Elapsed.TotalSeconds / 1e9;
        }
        return new[] { Serie(), Serie(), Serie() }.Order().ElementAt(1);
    }

    /// <summary>Le tampon de chaque fil du debit multi-fil.</summary>
    internal const long TamponDuDebit = 64L << 20;

    /// <summary>Pur : combien de fils mesurent le debit multi-fil. Au plus 8 et le nombre de coeurs, et jamais plus de la
    /// moitie de la memoire libre (meme regle que le test memoire : ne jamais faire paginer). Moins de deux fils ne
    /// saturent pas le controleur et ne peuvent pas dire « un seul canal » : 0, comme une memoire libre non lue.
    /// **Non chiffre.**</summary>
    internal static int FilsDuDebit(int coeurs, ulong? libreOctets)
    {
        if (libreOctets is not { } libre) return 0;
        var fils = (int)Math.Min(Math.Min(coeurs, 8), (long)(libre / 2 / TamponDuDebit));
        return fils >= 2 ? fils : 0;
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
