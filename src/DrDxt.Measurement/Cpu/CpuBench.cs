using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Measurement.Cpu;

/// <summary>Ce qu'une charge deterministe a rendu, avec sa dispersion.</summary>
/// <param name="Threads">Nombre de threads charges, chacun sur un cœur physique distinct.</param>
/// <param name="MopsParThread">Millions d'operations par seconde et par thread : la charge est en dependance stricte,
/// donc il suit l'horloge du cœur. Le total monte avec le nombre de threads et ne dit rien d'un bridage.</param>
/// <param name="EcartTypePourcent">Dispersion des passes, en pourcent de la moyenne : le plancher de ce que
/// l'instrument sait distinguer, sans lequel aucun A/B ne conclut.</param>
/// <param name="Somme">Somme de controle du travail effectue, identique d'une execution a l'autre a threads egaux.</param>
/// <param name="Passes">Nombre de passes effectuees.</param>
/// <param name="ChauffeSecondes">Duree de charge continue avant la premiere passe retenue.</param>
/// <param name="EpinglesReussis">Threads reellement epingles, au pire des passes retenues, jamais la derniere : un
/// echec en route laisse dans la moyenne un debit mesure sur un cœur choisi par l'ordonnanceur.</param>
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
/// Le banc CPU deterministe, a cote des bancs GPU (resolution 0,122 %) et memoire (1,83 % sur le debit).
/// Mesure du 09/09/2026 : en jeu, le goulot de cette machine est le chemin CPU (`gpuWait` a 0-3 % sur quatre series),
/// et en ligne le 1 % low s'etend sur ±45 % par tranches de 30 s : rien n'y est demontrable.
///
/// La charge legere est une chaine de dependances sur un seul registre : ni cache ni DRAM, c'est l'horloge qu'on lit.
/// La somme de controle empeche le compilateur de supprimer une boucle dont le resultat n'est pas lu (un banc
/// optimise en neant rend des chiffres magnifiques et stables) : rendue, comparee, un test echoue si elle change.
///
/// L'epinglage fait la repetabilite. La liste des processeurs vient de l'appelant (`CpuTopology`), jamais d'une
/// formule : `idx * 2` est vrai des huit P-cores de cette machine, mais au-dela tombe sur des cœurs lents (16, 18,
/// 20...) et se trompe sans SMT, sur AMD ou avec plusieurs groupes. Il est verifie : `SetThreadAffinityMask` rend 0
/// en cas d'echec, et le nombre de reussites sort avec le resultat.
///
/// Lecture pure : aucune ecriture, aucune elevation.
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
    /// <param name="passes">Passes mesurees. La premiere est jetee : elle etablit le boost et les caches d'instructions.</param>
    /// <param name="chauffeSecondes">Duree de charge continue avant de mesurer ; elle decide de ce qu'on mesure. Sur
    /// cette machine, huit cœurs chargés tiennent 4 798 MHz, glissent a 4 485 en douze secondes, puis tombent a
    /// 3 716 MHz vers la quinzieme et y restent (limite de turbo → limite soutenue). Des passes de deux secondes rendent
    /// 5 296 MHz et ne voient aucun bridage ; un jeu vit du mauvais cote de la marche (mesure en partie : 3 861 MHz).</param>
    /// <param name="processeurs">Les processeurs logiques a occuper, un par thread, dans l'ordre, depuis la topologie
    /// reelle. Absente ou trop courte, on retombe sur les indices consecutifs, et <c>EpinglesReussis</c> dit ce qui a
    /// vraiment ete epingle.</param>
    public static CpuBenchResult Measure(int threads, int passes = 5, ChargeCpu charge = ChargeCpu.Legere,
        double chauffeSecondes = 0, IReadOnlyList<int>? processeurs = null)
    {
        if (threads < 1) threads = 1;
        if (passes < 1) passes = 1;

        var debits = new List<double>(passes);
        ulong somme = 0;
        // Le minimum sur les passes retenues : un echec d'epinglage a la deuxieme ne s'efface pas derriere une cinquieme
        // reussie, alors que son debit reste dans la moyenne. Un echec pendant la chauffe ne salit aucun chiffre.
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

            // Pendant la chauffe, on travaille sans rien retenir : une passe gardee avant la marche melangerait les deux regimes.
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

    /// <summary>Une chaine de dependances stricte : chaque tour attend le precedent, aucune unite parallele ne
    /// l'etale, le temps suit l'horloge.</summary>
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
    /// La charge lourde. Mesure du 09/09/2026 : charge legere, huit cœurs a 5 297 MHz, debit par thread stable a 0,4 % ;
    /// des processus PowerShell sur les memes cœurs, 3 698 MHz. C'est la puissance appelee, pas le nombre de cœurs, qui
    /// fait tomber la frequence : un banc qui ne consomme rien ne mesure que le plafond de boost.
    /// Celle-ci multiplie sur 64 bits (unites larges) et balaie un tampon qui tient dans le L2 d'un cœur : du courant
    /// appele, sans dependre de la DRAM. Le tampon fait 192 Ko, mais l'index masque par 0x5FFF n'en atteint que
    /// 16 Ki elements, soit 128 Ko.
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
