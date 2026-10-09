using DrDxt.Detection.Cpu;
using DrDxt.Measurement.Cpu;
using DrDxt.Tools.Coeurs;

// ============================================================================================
// CHAQUE CŒUR REND-IL LA MEME CHOSE ?
//
// La meme charge deterministe, un cœur a la fois, epingle. C'est le seul moyen dont ce depot
// dispose pour approcher « une piece est-elle en train de lacher » sans MSR, sans temperature
// cœur lisible et sans pilote en anneau 0.
//
// ET LE RESULTAT N'EST JAMAIS UN DIAGNOSTIC. Une dispersion entre cœurs est ATTENDUE : Turbo
// Boost Max designe un ou deux cœurs FAVORIS, capables de monter plus haut que les autres, par
// construction. Un ecart est donc le comportement normal d'une puce saine ; l'outil dit « ce cœur
// se distingue », jamais « ce cœur est degrade ».
//
// LECTURE PURE : aucune ecriture, aucun reglage. Il occupe des cœurs, c'est tout.
//
//   dotnet run --project tools/coeurs -c Release -- [passesParCoeur]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var passes = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 4;

var carte = new CpuTopology().Lire();
if (carte.Count == 0)
{
    Console.WriteLine("   topologie CPU NON LUE : on ne devine pas quels cœurs comparer.");
    return 2;
}

var classes = CpuTopology.ClassesParPerformance(carte);
// Un processeur logique par cœur PHYSIQUE de la classe rapide : comparer un cœur a un jumeau SMT
// mesurerait le partage, pas le cœur.
var processeurs = carte
    .Where(x => x.ClasseEfficacite == classes[0] && x.Groupe == 0)
    .GroupBy(x => x.IndexCoeur)
    .Select(g => (int)g.OrderBy(x => x.IndexLogique).First().IndexLogique)
    .OrderBy(x => x)
    .ToList();

Console.WriteLine($"== CHAQUE CŒUR REND-IL LA MEME CHOSE == {processeurs.Count} cœurs de la classe "
                  + $"{CpuTopology.NommerClasse(classes[0], classes)}, {passes} passes chacun");
Console.WriteLine();
Console.WriteLine("   processeur   debit        dispersion   somme de controle");

var mesures = new List<DebitParCoeur>();
foreach (var cpu in processeurs)
{
    var r = CpuBench.Measure(1, passes, ChargeCpu.Lourde, 0, [cpu]);
    if (r.EpinglesReussis != 1)
    {
        Console.WriteLine($"   {cpu,10}   epinglage refuse — ce cœur n'est pas compare");
        continue;
    }
    mesures.Add(new DebitParCoeur(cpu, r.MopsParThread, r.EcartTypePourcent));
    Console.WriteLine($"   {cpu,10}   {r.MopsParThread,8:0.0} Mops   {r.EcartTypePourcent,7:0.00} %   0x{r.Somme:X16}");
    Thread.Sleep(1500);   // laisser le cœur redescendre avant le suivant
}

Console.WriteLine();
var verdict = CoeursHomogenes.Analyser(mesures);
Console.WriteLine($"   mediane {verdict.MedianeMops:0.0} Mops   bruit median {verdict.BruitPercent:0.00} %"
                  + $"   pire dispersion {verdict.PireDispersionPercent:0.00} %");
Console.WriteLine($"   le plus lent : processeur {verdict.CoeurLePlusLent}   "
                  + $"le plus rapide : processeur {verdict.CoeurLePlusRapide}");
Console.WriteLine();
Console.WriteLine("   => " + verdict.Lisible);
return verdict.MeriteUnRegard ? 1 : 0;
