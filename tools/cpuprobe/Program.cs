using System.Diagnostics;
using DrDxt.Detection.Cpu;
using DrDxt.Tools.CpuProbe;

// ============================================================================================
// CE QUE FONT LES THREADS DU JEU — et ce que ca ne prouve pas.
//
// Le GPU de cette machine reste a 88-90 % pendant Warzone, frequence stable, aucun plafond
// publie. Quelque chose l'empeche d'etre alimente en continu, et le produit n'avait aucun
// instrument pour regarder ce quelque chose.
//
// LECTURE PURE : OpenThread + GetThreadTimes + QueryThreadCycleTime. Aucune injection, aucun
// hook, aucun scanner memoire.
//
//   dotnet run --project tools/cpuprobe -- [pid] [secondes] [repetitions]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pid = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 0;
var secondes = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 5.0;
var repetitions = args.Length > 2 && int.TryParse(args[2], out var r) ? r : 3;

if (pid == 0)
{
    Console.WriteLine("Donne un pid. Ce probe ne devine pas quel processus t'interesse.");
    return 3;
}

// ── LA CARTE DES COEURS, LUE — jamais un tableau d'index code en dur ─────────────────────
var carte = new CpuTopology().Lire();
if (carte.Count == 0)
{
    Console.WriteLine("   topologie CPU : NON LUE (Unknown — surtout pas « un seul coeur »)");
}
else
{
    var classes = CpuTopology.ClassesParPerformance(carte);
    Console.WriteLine($"   topologie : {carte.Count} processeurs logiques, "
                      + $"{carte.Select(x => x.IndexCoeur).Distinct().Count()} coeurs physiques, "
                      + $"{classes.Count} classe(s) d'efficacite");
    foreach (var c in classes)
    {
        var membres = carte.Where(x => x.ClasseEfficacite == c).ToList();
        Console.WriteLine($"      EfficiencyClass {c} — {CpuTopology.NommerClasse(c, classes)} : "
                          + $"{membres.Count} threads logiques sur "
                          + $"{membres.Select(x => x.IndexCoeur).Distinct().Count()} coeurs "
                          + $"(index {string.Join(",", membres.Select(x => x.IndexLogique))})");
    }
    Console.WriteLine();
}

var processus = Process.GetProcessById(pid);
Console.WriteLine($"== THREADS DE {processus.ProcessName} (pid {pid}) ==");
Console.WriteLine($"   {Environment.ProcessorCount} threads logiques — fenetres de {secondes:0.#} s");
Console.WriteLine();

var lecteur = new ThreadCpuReader();
for (var i = 0; i < repetitions; i++)
{
    var e = lecteur.Observer(pid, TimeSpan.FromSeconds(secondes));
    if (e is null) { Console.WriteLine("   lecture impossible"); continue; }

    Console.WriteLine($"   fenetre {i + 1} — {e.ThreadsVus} threads mesures, "
                      + $"{e.PartDeLaMachine:P1} de la machine entiere");
    foreach (var t in e.ThreadsChauds)
    {
        // Les cycles PAR SECONDE DE CPU : si `QueryThreadCycleTime` etait derive du TSC, ce
        // rapport serait constant et ne porterait aucune information sur la frequence reelle. On
        // l'affiche pour pouvoir le VERIFIER au lieu de le supposer.
        var secondesCpu = t.PartDUnThreadLogique * e.Fenetre.TotalSeconds;
        var ghz = t.Cycles is { } cy && secondesCpu > 0 ? cy / secondesCpu / 1e9 : (double?)null;
        Console.WriteLine($"      tid {t.ThreadId,-8}{t.PartDUnThreadLogique,7:P2} d'un thread logique"
                          + (t.Cycles is { } c ? $"   {c / 1_000_000.0,8:0} Mcycles" : "")
                          + (ghz is { } g ? $"   {g,6:0.000} Gcycles/s CPU" : ""));
    }
    Console.WriteLine();
}

// ── LA RESIDENCE REELLE, PAR ETW ────────────────────────────────────────────────────────
// `GetThreadIdealProcessor` rend un processeur PREFERE, `GetThreadSelectedCpuSets` des
// ensembles ASSIGNES. Ni l'un ni l'autre ne dit ou le thread a passe son temps. Seule la
// commutation de contexte le dit — et c'est la seule chose qu'on lui demande.
if (carte.Count > 0)
{
    var chauds = lecteur.Observer(pid, TimeSpan.FromSeconds(2))?.ThreadsChauds
        .Take(3).Select(x => x.ThreadId).ToList() ?? new List<int>();
    if (chauds.Count > 0)
    {
        Console.WriteLine($"== RESIDENCE REELLE (ETW CSwitch, {secondes:0.#} s) ==");
        var trace = new ThreadResidencyTracer(carte)
            .Observer(chauds, TimeSpan.FromSeconds(secondes));
        var (issue, residences) = (trace.Issue, trace.Residences);

        if (issue != TraceOutcome.Ok)
        {
            Console.WriteLine($"   trace indisponible : {issue}"
                              + (trace.CodeWin32 != 0 ? $" (code Win32 {trace.CodeWin32})" : ""));
            Console.WriteLine("   Aucune residence n'est devinee a la place.");
        }
        else
        {
            var classes = CpuTopology.ClassesParPerformance(carte);
            foreach (var res in residences.OrderByDescending(x => x.TempsExecuteSecondes))
            {
                Console.WriteLine($"   tid {res.ThreadId,-8}{res.TempsExecuteSecondes,6:0.00} s executees   "
                                  + $"{res.Commutations,6} commutations   {res.Migrations,5} migrations");
                foreach (var c in classes)
                    Console.WriteLine($"      {CpuTopology.NommerClasse(c, classes),-32}"
                                      + $"{res.Part(c) ?? 0,7:P1}"
                                      + $"   ({res.ParClasse.GetValueOrDefault(c):0.00} s)");
            }
        }
        Console.WriteLine();
    }
}

Console.WriteLine("   Le 14700F est HYBRIDE : « 95 % d'un thread logique » ne dit PAS « un P-core");
Console.WriteLine("      est sature » — le thread a pu tourner sur un E-core, ou migrer. Et une");
Console.WriteLine("      consommation elevee n'est pas une cause : c'est une coincidence a verifier.");
return 0;
