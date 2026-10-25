using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Measurement.Cpu;

/// <summary>Ce qu'une charge deterministe a rendu, avec sa dispersion.</summary>
/// <param name="Threads">Nombre de threads charges, chacun sur un cœur physique distinct.</param>
/// <param name="MopsParThread">Millions d'operations par seconde et par thread. C'est la
/// grandeur qui suit la frequence : la charge est en dependance stricte, donc un thread avance
/// proportionnellement a l'horloge de son cœur. Le total, lui, monte forcement avec le nombre de
/// threads et ne dit rien d'un bridage.</param>
/// <param name="EcartTypePourcent">Dispersion des passes, en pourcent de la moyenne. Sans elle,
/// aucun A/B ne peut conclure : c'est le plancher de ce que l'instrument sait distinguer.</param>
/// <param name="Somme">Somme de controle du travail effectue. Elle doit etre identique d'une
/// execution a l'autre pour le meme nombre de threads.</param>
/// <param name="Passes">Nombre de passes effectuees.</param>
/// <param name="ChauffeSecondes">Duree de charge continue avant la premiere passe retenue.</param>
/// <param name="EpinglesReussis">Threads reellement epingles, au pire des passes retenues.
/// Le pire, jamais la derniere : un echec en cours de route laisse un debit mesure sur un cœur
/// que l'ordonnanceur a choisi seul, et une derniere passe reussie l'effacerait du compte tout en
/// le gardant dans la moyenne.</param>
public sealed record CpuBenchResult(
    int Threads, double MopsParThread, double EcartTypePourcent, ulong Somme, int Passes, bool Lourde,
    double ChauffeSecondes, int EpinglesReussis);

/// <summary>La charge a executer.</summary>
public enum ChargeCpu
{
    /// <summary>Chaine de dependances sur un registre. Consomme peu : elle sonde la frequence
    /// maximale accessible, pas le comportement sous budget.</summary>
    Legere,

    /// <summary>Multiplications larges plus balayage d'un tampon qui tient dans le L2. Consomme
    /// beaucoup plus, et c'est le seul moyen de reproduire un budget de puissance.</summary>
    Lourde,
}

/// <summary>
/// Le banc CPU deterministe : l'instrument sans lequel les hypotheses CPU restent indecidables.
///
/// ══ pourquoi il existe ══
/// Le depot possede un banc GPU deterministe (resolution 0,122 %) et un banc memoire (1,83 % sur
/// le debit). Celui-ci couvre le processeur : la mesure du 09/09/2026 montre que
/// le goulot de cette machine en jeu est le chemin CPU (`gpuWait` a 0-3 % sur quatre series).
/// Toute question CPU devait donc se juger en partie en ligne, ou l'etendue mesuree du 1 % low
/// atteint ±45 % sur des tranches de 30 s : rien n'y est demontrable.
///
/// ══ ce que la charge mesure, et ce qu'elle evite ══
/// Une chaine de dependances entiere sur un seul registre : chaque operation attend la
/// precedente. Aucune memoire au-dela d'un registre, donc ni cache ni DRAM dans le resultat :
/// c'est l'horloge qu'on lit, pas le sous-systeme memoire (celui-la a deja son banc).
///
/// La somme de controle n'est pas un ornement. Une boucle arithmetique dont le resultat n'est
/// pas lu est une boucle que le compilateur a le droit de supprimer entierement. Un banc optimise
/// en neant rend des chiffres magnifiques et parfaitement stables : le mode d'echec le plus
/// difficile a voir. La somme est rendue, comparee, et un test echoue si elle change.
///
/// ══ l'epinglage ══
/// Sans epinglage, l'ordonnanceur deplace les threads et la dispersion explose : la repetabilite
/// est la seule raison d'etre d'un banc.
///
/// Mais la liste des processeurs vient de l'appelant, jamais d'une formule. Epingler sur
/// `idx * 2`, en supposant « un indice logique sur deux = un cœur physique rapide », est vrai des
/// huit P-cores de cette machine et faux partout ailleurs : au-dela du huitieme thread la formule
/// tomberait sur des cœurs lents (16, 18, 20...) tout en annoncant « 12 cœurs rapides », et elle
/// serait fausse sur un CPU sans SMT, sur AMD, ou avec plusieurs groupes de processeurs.
/// `CpuTopology` publie la reponse ; la deviner est le defaut que ce depot s'interdit ailleurs.
///
/// Et l'epinglage est verifie : `SetThreadAffinityMask` rend 0 en cas d'echec, et le nombre de
/// reussites sort avec le resultat. Un banc qui croit epingler sans y parvenir rend une dispersion
/// inexplicable et aucune trace de la cause.
///
/// Lecture pure : aucune ecriture, aucune elevation. Il occupe des cœurs, c'est tout.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CpuBench
{
    /// <summary>Iterations par passe et par thread. Calibre pour ~250 ms sur un cœur a 5 GHz :
    /// assez long pour que le boost s'etablisse, assez court pour qu'une passe ne coute rien.</summary>
    private const long Iterations = 300_000_000;

    /// <summary>Graine fixe : deux executions doivent rendre la meme somme, ou l'instrument ment.</summary>
    private const ulong Graine = 0x9E3779B97F4A7C15;

    /// <summary>Mesure <paramref name="threads"/> cœurs charges simultanement.</summary>
    /// <param name="passes">Passes mesurees. La premiere est jetee : elle sert a etablir le boost
    /// et les caches d'instructions, et l'inclure gonflerait la dispersion sans rien apprendre.</param>
    /// <param name="chauffeSecondes">Duree de charge continue avant de mesurer.
    ///
    /// Ce parametre n'est pas un confort, il decide de ce qu'on mesure. Sur cette machine
    ///, huit cœurs sous charge continue tiennent 4 798 MHz au depart, glissent a
    /// 4 485 en douze secondes, puis tombent d'une marche a 3 716 MHz vers la quinzieme seconde et
    /// y restent. C'est le passage de la limite de turbo a la limite soutenue. Un banc dont les
    /// passes durent deux secondes ne sort jamais du turbo : il rend 5 296 MHz et conclut
    /// tranquillement que rien ne bride. Un jeu, lui, vit du mauvais cote de cette marche
    /// (mesure en partie : 3 861 MHz). Pour juger ce qu'un jeu obtient, il faut chauffer.</param>
    /// <param name="processeurs">Les processeurs logiques a occuper, un par thread, dans l'ordre.
    /// Fournis par l'appelant depuis la topologie reelle. Quand la liste est absente ou trop
    /// courte, on retombe sur les indices consecutifs, et <c>EpinglesReussis</c> dit alors ce qui
    /// a vraiment ete epingle, au lieu de le supposer.</param>
    public static CpuBenchResult Measure(int threads, int passes = 5, ChargeCpu charge = ChargeCpu.Legere,
        double chauffeSecondes = 0, IReadOnlyList<int>? processeurs = null)
    {
        if (threads < 1) threads = 1;
        if (passes < 1) passes = 1;

        var debits = new List<double>(passes);
        ulong somme = 0;
        // Le pire cas sur les passes retenues, pas la derniere. Ecraser ce compteur a chaque passe
        // ferait disparaitre un echec d'epinglage a la deuxieme passe derriere une cinquieme reussie,
        // et son debit (mesure sur un cœur que l'ordonnanceur a choisi seul) resterait dans la moyenne
        // et alimenterait le verdict. On garde donc le minimum, et
        // seulement sur les passes qui comptent : un echec pendant la chauffe ne salit aucun
        // chiffre, il ne fait que rechauffer ailleurs.
        var reussites = int.MaxValue;
        var finChauffe = DateTime.UtcNow.AddSeconds(chauffeSecondes);
        var enChauffe = chauffeSecondes > 0;

        for (var passe = 0; passe <= passes || enChauffe; passe++)
        {
            var sommes = new ulong[threads];
            var epingles = new bool[threads];
            var equipe = new Thread[threads];
            var barriere = new Barrier(threads + 1);

            for (var i = 0; i < threads; i++)
            {
                var idx = i;
                equipe[i] = new Thread(() =>
                {
                    // Le processeur vient de la topologie quand l'appelant l'a fournie.
                    epingles[idx] = Epingler(processeurs is not null && idx < processeurs.Count
                        ? processeurs[idx]
                        : idx);
                    barriere.SignalAndWait();       // tout le monde demarre ensemble
                    sommes[idx] = charge == ChargeCpu.Lourde
                        ? TravailLourd(Graine + (ulong)idx)
                        : Travail(Graine + (ulong)idx);
                    barriere.SignalAndWait();
                }) { IsBackground = true, Priority = ThreadPriority.Highest };
                equipe[i].Start();
            }

            barriere.SignalAndWait();               // top depart
            var chrono = Stopwatch.StartNew();
            barriere.SignalAndWait();               // tout le monde a fini
            chrono.Stop();

            foreach (var t in equipe) t.Join();

            if (passe == 0) { somme = Melanger(sommes); continue; }   // passe d'echauffement, jetee

            // Tant que la chauffe n'est pas finie, on travaille sans rien retenir : la charge doit
            // etre continue pour franchir la marche, et une passe gardee avant serait un melange
            // des deux regimes.
            if (enChauffe)
            {
                if (DateTime.UtcNow < finChauffe) { passe = 0; continue; }
                enChauffe = false;
                passe = 0;
                continue;
            }
            reussites = Math.Min(reussites, epingles.Count(e => e));
            debits.Add(Iterations / chrono.Elapsed.TotalSeconds / 1e6);
        }

        var moyenne = debits.Average();
        var sigma = debits.Count > 1
            ? Math.Sqrt(debits.Sum(d => (d - moyenne) * (d - moyenne)) / (debits.Count - 1))
            : 0;

        return new CpuBenchResult(threads, moyenne, moyenne > 0 ? sigma / moyenne * 100 : 0, somme,
            debits.Count, charge == ChargeCpu.Lourde, chauffeSecondes,
            reussites == int.MaxValue ? 0 : reussites);
    }

    /// <summary>La charge : une chaine de dependances stricte. Chaque tour a besoin du resultat du
    /// precedent, donc le processeur ne peut pas l'etaler sur ses unites paralleles : le temps est
    /// gouverne par l'horloge, ce qu'on veut mesurer.</summary>
    private static ulong Travail(ulong graine)
    {
        var x = graine;
        for (long i = 0; i < Iterations; i++)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            x += 0x2545F4914F6CDD1D;
        }
        return x;
    }

    /// <summary>
    /// La charge lourde, et pourquoi elle existe.
    ///
    /// Mesure du 09/09/2026 : avec la charge legere, huit cœurs charges tournent a 5 297 MHz et
    /// le debit par thread ne bouge pas de 0,4 %. Avec des processus PowerShell sur les memes huit
    /// cœurs, la frequence tombait a 3 698 MHz. Les deux mesures sont justes : ce n'est pas le
    /// nombre de cœurs qui fait tomber la frequence, c'est la puissance que la charge appelle.
    /// Conclure « la frequence s'effondre avec le nombre de cœurs » prendrait une correlation reelle
    /// pour une cause.
    ///
    /// Un banc qui ne consomme rien mesure donc le plafond de boost et rien d'autre. Celui-ci
    /// multiplie sur 64 bits (unites larges sollicitees) et balaie un tampon qui tient dans le L2 d'un cœur : assez de
    /// trafic pour appeler du courant, pas assez pour que le resultat depende de la DRAM : la memoire a deja son propre
    /// banc. Le tampon fait 192 Ko, mais l'index masque par 0x5FFF n'en atteint que 16 Ki elements, soit 128 Ko.
    /// </summary>
    private static ulong TravailLourd(ulong graine)
    {
        var tampon = new ulong[24 * 1024];          // 192 Ko alloues, 128 Ko balayes : dans le L2, hors du L1
        for (var i = 0; i < tampon.Length; i++) tampon[i] = graine + (ulong)i * 0x9E3779B9;

        var x = graine;
        var tours = Iterations / 8;                 // chaque tour fait ~8 fois plus de travail
        for (long i = 0; i < tours; i++)
        {
            var j = (int)(x & (ulong)(tampon.Length - 1));
            var v = tampon[j];
            x = x * 0x5851F42D4C957F2D + v;         // multiplication 64 bits : unite large
            x ^= x >> 29;
            x = x * 0x94D049BB133111EB + 1;
            x ^= x >> 32;
            tampon[j] = x;                          // ecriture : le tampon reste chaud et vivant
        }
        ulong h = x;
        for (var i = 0; i < tampon.Length; i += 512) h = h * 31 + tampon[i];
        return h;
    }

    private static ulong Melanger(ulong[] valeurs)
    {
        ulong h = 0;
        foreach (var v in valeurs) h = h * 31 + v;
        return h;
    }

    /// <summary>Epingle le thread courant au processeur logique demande, et dit si ca a marche.
    /// <c>SetThreadAffinityMask</c> rend l'ancien masque, ou 0 en cas d'echec.</summary>
    private static bool Epingler(int indexLogique)
    {
        if (indexLogique >= Environment.ProcessorCount || MasqueDe(indexLogique) is not { } masque) return false;
        try { return SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)masque) != UIntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>Le masque d'affinité d'un processeur logique du groupe courant, ou null hors des 64 qu'un masque tient : en C#,
    /// <c>1UL &lt;&lt; 64</c> vaut <c>1UL &lt;&lt; 0</c>, et le banc se serait épinglé sur le processeur 0.</summary>
    internal static ulong? MasqueDe(int indexLogique)
        => indexLogique is >= 0 and < 64 ? 1UL << indexLogique : null;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
}
