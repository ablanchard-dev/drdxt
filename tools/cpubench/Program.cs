using DrDxt.Detection.Cpu;
using DrDxt.Measurement.Cpu;
using DrDxt.Tools.CpuBench;

// ============================================================================================
// LE BANC CPU DETERMINISTE — et la courbe qui dit si la puissance bride ce processeur.
//
// POURQUOI CE BANC EXISTE. Le 09/09/2026, une heure de mesure EN PARTIE EN LIGNE a produit un
// faux gain : deux A/B/A identiques ont rendu « +42 % » puis « -70 % » sur le 1 % low, et les
// temoins seuls variaient de 32 a 84 images. La meme question, posee a une charge DETERMINISTE,
// a ete tranchee en trois minutes a 450 fois le bruit. Ce banc est la traduction de cette lecon.
//
// ══ LA COURBE, ET CE QU'ELLE SEPARE — AVEC UNE CONDITION ══
// On charge 1, 2, 4, 8 puis 12 cœurs, et on regarde le debit PAR THREAD. S'il s'effondre quand on
// ajoute des cœurs, un BUDGET (puissance ou thermique) bride ; s'il reste plat, il n'y en a pas.
//
// MAIS CETTE LECTURE N'EST VALABLE QU'AVEC `--lourd`, ET CA A ETE PAYE LE 09/09/2026. Avec la
// charge legere, huit cœurs charges tournent a 5 297 MHz et le debit par thread ne bouge pas de
// 0,4 % — le banc conclurait « rien ne bride ». Les memes huit cœurs, charges par des processus
// PowerShell, tombaient a 3 698 MHz. Les deux mesures etaient justes : **ce n'est pas le NOMBRE de
// cœurs qui fait tomber la frequence, c'est la PUISSANCE que la charge appelle.** Un banc qui ne
// consomme rien mesure le plafond de boost et se tait sur le budget.
//
// ══ LE TEMOIN DE RETOUR ══
// La derniere mesure REPETE la premiere. Si elle n'y revient pas, la machine a derive pendant la
// manip et la courbe ne vaut rien : c'est cet ecart-la, et non une intuition, qui dit ce que le
// banc sait distinguer. Il est affiche AVANT le verdict, jamais apres.
//
// LECTURE PURE : aucune ecriture, aucune elevation. Il occupe des cœurs, c'est tout.
//
//   dotnet run --project tools/cpubench -c Release -- [passesParPoint] [--lourd]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var passes = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5;
var charge = args.Any(a => a.Equals("--lourd", StringComparison.OrdinalIgnoreCase))
    ? ChargeCpu.Lourde : ChargeCpu.Legere;
// 35 s : la marche a ete observee vers la 15e seconde, on la franchit largement.
var chauffe = args.Any(a => a.Equals("--soutenu", StringComparison.OrdinalIgnoreCase)) ? 35.0 : 0.0;

var carte = new CpuTopology().Lire();

// LES PROCESSEURS VIENNENT DE LA TOPOLOGIE, JAMAIS D'UNE FORMULE. Une premiere version prenait
// « un indice logique sur deux » : vrai des huit P-cores de cette machine, faux des le neuvieme
// thread (il tombait sur des cœurs LENTS tout en annoncant « 12 cœurs rapides »), et faux sur tout
// CPU sans SMT. On prend UN processeur logique par cœur PHYSIQUE, classe la plus rapide d'abord.
List<int> processeurs;
var coeursRapides = 0;
if (carte.Count > 0)
{
    var classes = CpuTopology.ClassesParPerformance(carte);
    processeurs = carte
        .GroupBy(x => (x.Groupe, x.IndexCoeur))
        .Select(g => g.OrderBy(x => x.IndexLogique).First())
        .OrderByDescending(x => x.ClasseEfficacite)      // les rapides d'abord
        .ThenBy(x => x.IndexLogique)
        .Where(x => x.Groupe == 0)                       // hors du groupe 0, le masque d'affinite change de sens
        .Select(x => (int)x.IndexLogique)
        .ToList();
    coeursRapides = carte.Where(x => x.ClasseEfficacite == classes[0])
                         .Select(x => x.IndexCoeur).Distinct().Count();
    Console.WriteLine($"== BANC CPU DETERMINISTE == {carte.Count} processeurs logiques, "
                      + $"{coeursRapides} cœurs de la classe {CpuTopology.NommerClasse(classes[0], classes)}");
}
else
{
    // Topologie non lue : on ne DEVINE pas les classes, on le dit et on prend les indices bruts.
    processeurs = Enumerable.Range(0, Environment.ProcessorCount).ToList();
    Console.WriteLine("== BANC CPU DETERMINISTE == topologie NON LUE — les cœurs charges ne sont pas");
    Console.WriteLine("   choisis, seulement occupes dans l'ordre. La courbe melange les classes.");
}

var maxThreads = Math.Max(1, processeurs.Count);
var points = new[] { 1, 2, 4, 8, 12 }.Where(n => n <= maxThreads).ToList();
if (points.Count == 0) points.Add(1);
points.Add(points[0]);   // temoin de retour

Console.WriteLine($"   {passes} passes par point, epinglage sur des cœurs physiques distincts");
Console.WriteLine(charge == ChargeCpu.Lourde
    ? "   charge LOURDE (multiplications larges + tampon L2) - celle qui appelle du courant"
    : "   charge LEGERE (chaine de dependances) - elle sonde le plafond de boost, PAS le budget.");
if (charge == ChargeCpu.Legere)
    Console.WriteLine("   Pour savoir si la puissance bride cette machine, il FAUT --lourd.");
Console.WriteLine(chauffe > 0
    ? $"   regime SOUTENU : {chauffe:0} s de charge continue avant de mesurer"
    : "   regime RAFALE : les passes durent quelques secondes, la machine ne quitte jamais son turbo."
      + " Ajouter --soutenu pour mesurer ce qu'un JEU obtient.");
Console.WriteLine();
Console.WriteLine("   threads   debit/thread   dispersion   somme de controle");

var mesures = new List<CpuBenchResult>();
foreach (var n in points)
{
    var r = CpuBench.Measure(n, passes, charge, chauffe, processeurs);
    mesures.Add(r);
    var epinglage = r.EpinglesReussis == r.Threads ? "" : $"   {r.EpinglesReussis}/{r.Threads} epingles";
    if (coeursRapides > 0 && r.Threads > coeursRapides) epinglage += "   deborde sur la classe LENTE";
    Console.WriteLine($"   {r.Threads,7}   {r.MopsParThread,9:0.0} Mops   {r.EcartTypePourcent,8:0.00} %   0x{r.Somme:X16}{epinglage}");
    Thread.Sleep(2000);   // laisser la machine redescendre entre deux points
}

Console.WriteLine();

// Le raisonnement vit dans DrDxt.Measurement.Cpu : un verdict qu'on ne peut pas tester est un
// verdict qu'on repayera, et celui-ci a deja menti trois fois.
var verdict = CpuBudgetJuge.Juger(mesures, coeursRapides);

Console.WriteLine($"   TEMOIN DE RETOUR : {mesures[0].Threads} thread(s) au debut {mesures[0].MopsParThread:0.0} Mops, "
                  + $"a la fin {mesures[^1].MopsParThread:0.0} Mops  ->  derive {verdict.DerivePercent:0.00} %");
Console.WriteLine($"   PLANCHER DU BANC : la plus grande dispersion vue est "
                  + $"{mesures.Max(m => m.EcartTypePourcent):0.00} %.");
Console.WriteLine("   Tout ecart plus petit que ces deux nombres ne prouve RIEN.");
Console.WriteLine();

if (verdict.Issue == CpuBudgetIssue.ChargesIncomparables)
{
    Console.WriteLine("   SOMMES DE CONTROLE DIVERGENTES a nombre de threads egal : la charge n'a pas");
    Console.WriteLine("      fait le meme travail d'une passe a l'autre. Les debits ne sont pas comparables.");
    return 1;
}

if (verdict.Issue != CpuBudgetIssue.Indetermine)
{
    Console.WriteLine($"   {verdict.ThreadsCompares.De} thread -> {verdict.ThreadsCompares.A} threads : "
                      + $"{verdict.ChutePercent:+0.0;-0.0;0} % de debit PAR THREAD   (cœurs de MEME classe)");
}
if (verdict.PointMixte is { } mixte)
{
    var chuteMixte = (mixte.MopsParThread - mesures[0].MopsParThread) / mesures[0].MopsParThread * 100;
    Console.WriteLine($"   {mesures[0].Threads} thread -> {mixte.Threads} threads : {chuteMixte:+0.0;-0.0;0} %"
                      + "   MELANGE deux classes de cœurs — ne sert pas au verdict");
}
Console.WriteLine();

switch (verdict.Issue)
{
    case CpuBudgetIssue.BudgetEtabli:
        Console.WriteLine($"   => EFFET ETABLI : |{verdict.ChutePercent:0.0} %| depasse le seuil de bruit "
                          + $"({verdict.SeuilPercent:0.0} %).");
        if (verdict.ChutePercent < 0)
        {
            Console.WriteLine("      Le debit par thread S'EFFONDRE quand on charge des cœurs : signature d'un");
            Console.WriteLine("      BUDGET (puissance ou thermique), jamais d'un multiplicateur fixe.");
            Console.WriteLine("      Ce banc ne dit PAS lequel des deux : la puissance du paquet et la");
            Console.WriteLine("         temperature cœur exigent le MSR.");
        }
        return 0;

    case CpuBudgetIssue.Indetermine:
        Console.WriteLine("   => INDETERMINE. Ce banc n'a pas de comparaison valide a offrir :");
        foreach (var e in verdict.Empechements) Console.WriteLine($"      - {e}");
        Console.WriteLine("      Aucun verdict n'est rendu. Un « rien ne bride » tire d'ici serait un mensonge");
        Console.WriteLine("      confortable, et c'est precisement ce que ce banc existe pour ne pas faire.");
        return 2;

    default:
        Console.WriteLine($"   => AUCUN EFFET AU-DESSUS DE {verdict.SeuilPercent:0.0} %. C'est une borne, pas");
        Console.WriteLine($"      une absence : un budget qui couterait moins de {verdict.SeuilPercent:0.0} %");
        Console.WriteLine("      resterait invisible a ce banc.");
        return 0;
}
