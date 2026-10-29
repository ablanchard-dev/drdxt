using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Cpu;

/// <summary>Ce qu'un thread a consomme pendant la fenetre observee.</summary>
/// <param name="ThreadId">L'identifiant systeme du thread.</param>
/// <param name="PartDUnThreadLogique">Sa consommation, en part d'un seul thread logique. 1,0 = il a
/// tourne sans interruption. Ce n'est pas un pourcentage de la machine.</param>
/// <param name="Cycles">Les cycles consommes, quand `QueryThreadCycleTime` a repondu. Le cycle est
/// insensible a la frequence, donc comparable entre deux fenetres ou l'horloge a bouge.</param>
public sealed record ThreadCpuSample(int ThreadId, double PartDUnThreadLogique, ulong? Cycles);

/// <summary>Ce que le processus entier a consomme pendant la fenetre.</summary>
/// <param name="Fenetre">La duree reellement observee.</param>
/// <param name="ThreadsLogiques">Le nombre de threads logiques de la machine.</param>
/// <param name="PartDeLaMachine">Consommation totale du processus, en part de la machine entiere.</param>
/// <param name="ThreadsChauds">Les threads les plus consommateurs, du plus chaud au moins chaud.</param>
/// <param name="ThreadsVus">Combien de threads ont pu etre mesures.</param>
public sealed record ProcessCpuSample(
    TimeSpan Fenetre,
    int ThreadsLogiques,
    double PartDeLaMachine,
    IReadOnlyList<ThreadCpuSample> ThreadsChauds,
    int ThreadsVus);

/// <summary>
/// Ce que fait le thread principal d'un jeu, sans y toucher. Pendant Warzone, le GPU de cette machine reste a 88-90 %,
/// frequence immobile, aucun plafond publie : quelque chose l'empeche d'etre alimente en continu.
/// Seulement `OpenThread(THREAD_QUERY_LIMITED_INFORMATION)`, `GetThreadTimes` et `QueryThreadCycleTime` : API
/// documentees, espace utilisateur, sans elevation. **Aucune injection, aucun hook, aucun scanner memoire** : la regle
/// du depot, et la seule compatible avec un anti-triche.
/// Le 14700F est hybride (huit P-cores, douze E-cores) : 95 % d'un thread logique ne dit pas « un P-core est sature »
/// (E-core, migration). Une part de thread logique, jamais un verdict sur un type de coeur ; et une consommation elevee
/// pendant que le GPU attend est une coincidence a verifier, pas une cause.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThreadCpuReader
{
    private const int ThreadQueryLimitedInformation = 0x0800;

    /// <summary>Observe un processus pendant <paramref name="fenetre"/>. Rend <c>null</c> si le
    /// processus disparait ou n'est pas lisible, jamais des zeros.</summary>
    public ProcessCpuSample? Observer(int pid, TimeSpan fenetre, int combienDeChauds = 6)
    {
        try
        {
            using var processus = Process.GetProcessById(pid);
            var avant = Instantane(processus);
            var chrono = Stopwatch.StartNew();
            Thread.Sleep(fenetre);
            chrono.Stop();
            processus.Refresh();
            var apres = Instantane(processus);

            var ecoule = chrono.Elapsed.TotalSeconds;
            if (ecoule <= 0) return null;

            var deltas = new List<ThreadCpuSample>();
            foreach (var (tid, apresT) in apres)
            {
                if (!avant.TryGetValue(tid, out var avantT)) continue;   // thread ne durant pas la fenetre
                var secondes = (apresT.Noyau + apresT.Utilisateur - avantT.Noyau - avantT.Utilisateur)
                               / 10_000_000.0;   // les temps Win32 sont en unites de 100 ns
                if (secondes < 0) continue;      // compteur reinitialise : on ne devine pas
                deltas.Add(new ThreadCpuSample(tid, secondes / ecoule,
                    apresT.Cycles is { } c && avantT.Cycles is { } a && c >= a ? c - a : null));
            }

            var logiques = Environment.ProcessorCount;
            return new ProcessCpuSample(
                chrono.Elapsed,
                logiques,
                deltas.Sum(d => d.PartDUnThreadLogique) / Math.Max(1, logiques),
                deltas.OrderByDescending(d => d.PartDUnThreadLogique).Take(combienDeChauds).ToList(),
                deltas.Count);
        }
        catch { return null; }
    }

    private static Dictionary<int, (long Noyau, long Utilisateur, ulong? Cycles)> Instantane(Process p)
    {
        var sortie = new Dictionary<int, (long, long, ulong?)>();
        foreach (ProcessThread t in p.Threads)
        {
            var poignee = OpenThread(ThreadQueryLimitedInformation, false, (uint)t.Id);
            if (poignee == IntPtr.Zero) continue;
            try
            {
                if (!GetThreadTimes(poignee, out _, out _, out var noyau, out var utilisateur))
                    continue;
                // Optionnel : une lecture ratee rend null.
                ulong? cycles = QueryThreadCycleTime(poignee, out var c) ? c : null;
                sortie[t.Id] = (ToLong(noyau), ToLong(utilisateur), cycles);
            }
            finally { CloseHandle(poignee); }
        }
        return sortie;
    }

    private static long ToLong(FILETIME f) => ((long)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread,
        out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycleTime);

}
