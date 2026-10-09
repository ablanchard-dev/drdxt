using System.Diagnostics;
using System.Runtime.InteropServices;
using DrDxt.App.Platform;

// -----------------------------------------------------------------------
// CE QUE COUTE LA LISTE DES VOISINS — 11/09/2026.
//
// CE QUI A DECLENCHE CE BANC. Le soir du 11/09 on a MESURE qu'OBS et nos compilations coutaient
// ~34 % et ~16 % de FPS pendant la partie. Le lot 6c a donc ajoute a l'ecran de scan la
// liste de ce qui tourne a cote. Cette liste se mesure PENDANT que le jeu tourne — 600 ms d'attente,
// deux enumerations de processus, une lecture des compteurs « GPU Engine ». Son propre cout n'avait
// jamais ete mesure. Ce serait ironique de couter au joueur ce qu'on lui reproche.
//
// Ce banc execute la vraie classe et mesure SON temps processeur, pas celui de ce qu'elle observe :
//   * le temps processeur du THREAD qui l'execute (GetThreadTimes, noyau + utilisateur) ;
//   * le temps processeur du PROCESSUS entier en differences (les compteurs de perf peuvent poser du
//     travail sur d'autres threads : le processus le voit, le thread non) ;
//   * la part d'UN coeur sur la duree d'un scan de 20 s.
// -----------------------------------------------------------------------

const int Repetitions = 5;
const double DureeScanSecondes = 20.0;   // la fenetre de mesure du scan (ScanViewModel)

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

[DllImport("kernel32.dll")]
static extern IntPtr GetCurrentThread();

static TimeSpan TempsDuThread()
{
    if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var noyau, out var utilisateur))
        throw new InvalidOperationException("GetThreadTimes a echoue");
    return TimeSpan.FromTicks(noyau + utilisateur);
}

var cible = args.Length > 0 && int.TryParse(args[0], out var pid) ? pid : (int?)null;
var moi = Process.GetCurrentProcess();
var lecteur = new VoisinsWin32();

Console.WriteLine($"cible exclue : {(cible?.ToString() ?? "aucune")} | repetitions : {Repetitions}");
Console.WriteLine("mur(ms)  thread-cpu(ms)  processus-cpu(ms)  voisins");

var totalThread = TimeSpan.Zero;
var totalProcessus = TimeSpan.Zero;
var totalMur = TimeSpan.Zero;
for (var i = 0; i < Repetitions; i++)
{
    var avantThread = TempsDuThread();
    moi.Refresh();
    var avantProcessus = moi.TotalProcessorTime;
    var chrono = Stopwatch.StartNew();

    var vus = lecteur.Lire(cible, CancellationToken.None);
    // La lecture rend null quand rien n'a pu être lu.
    foreach (var v in vus ?? []) Console.WriteLine($"    voisin {v.Nom} cpu={v.ProcesseurPourcent} gpu={(v.GpuPourcent?.ToString() ?? "null")}");

    chrono.Stop();
    var dThread = TempsDuThread() - avantThread;
    moi.Refresh();
    var dProcessus = moi.TotalProcessorTime - avantProcessus;

    // Le premier tour paie le chargement des assemblys et l'ouverture des compteurs : il est montre
    // mais il ne compte pas dans la moyenne, un scan reel n'est jamais le premier appel du process.
    Console.WriteLine($"{chrono.Elapsed.TotalMilliseconds,7:F1}  {dThread.TotalMilliseconds,13:F1}  "
                      + $"{dProcessus.TotalMilliseconds,16:F1}  {(vus is null ? "non lu" : vus.Count.ToString())}{(i == 0 ? "   (tour a froid)" : "")}");
    if (i == 0) continue;
    totalThread += dThread;
    totalProcessus += dProcessus;
    totalMur += chrono.Elapsed;
}

// QUI PAIE ? Les memes appels que la classe, chronometres separement, pour savoir quoi reduire.
{
    var c = Stopwatch.StartNew();
    foreach (var p in Process.GetProcesses()) { try { _ = p.TotalProcessorTime; } catch { } finally { p.Dispose(); } }
    c.Stop();
    var enumeration = c.Elapsed;

    c.Restart();
    var instances = System.Diagnostics.PerformanceCounterCategory.Exists("GPU Engine")
        ? new System.Diagnostics.PerformanceCounterCategory("GPU Engine").GetInstanceNames()
        : Array.Empty<string>();
    var apresNoms = c.Elapsed;
    foreach (var instance in instances.Where(i => i.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)))
    {
        try
        {
            using var compteur = new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
            _ = compteur.NextValue();
        }
        catch { }
    }
    c.Stop();
    var unParInstance = c.Elapsed - apresNoms;

    c.Restart();
    var snap = new System.Diagnostics.PerformanceCounterCategory("GPU Engine").ReadCategory();
    c.Stop();
    var lot = c.Elapsed;
    var util = snap["Utilization Percentage"];
    var exemples = util.Values.Cast<System.Diagnostics.InstanceData>().Take(3)
        .Select(d => $"{d.InstanceName}={d.RawValue}");
    Console.WriteLine();
    Console.WriteLine($"ReadCategory (1 appel, {util.Values.Count} instances) : {lot.TotalMilliseconds:F1} ms | {string.Join(" ", exemples)}");

    Console.WriteLine($"detail : 1 enumeration de processus {enumeration.TotalMilliseconds:F1} ms | "
                      + $"noms des instances GPU {apresNoms.TotalMilliseconds:F1} ms | "
                      + $"lecture des {instances.Count(i => i.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))} compteurs GPU "
                      + $"{unParInstance.TotalMilliseconds:F1} ms (l'ancienne methode)");
}

var n = Repetitions - 1;
var cpuMoyen = totalProcessus.TotalMilliseconds / n;
Console.WriteLine();
Console.WriteLine($"moyenne (hors tour a froid) : mur {totalMur.TotalMilliseconds / n:F1} ms, "
                  + $"thread {totalThread.TotalMilliseconds / n:F1} ms, processus {cpuMoyen:F1} ms");
Console.WriteLine($"part d'UN coeur sur un scan de {DureeScanSecondes:F0} s : "
                  + $"{cpuMoyen / (DureeScanSecondes * 1000.0) * 100.0:F3} %");
Console.WriteLine($"part d'UN coeur pendant sa propre fenetre de {VoisinsWin32.Fenetre.TotalMilliseconds:F0} ms : "
                  + $"{cpuMoyen / VoisinsWin32.Fenetre.TotalMilliseconds * 100.0:F1} %");
