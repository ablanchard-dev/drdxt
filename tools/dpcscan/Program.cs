using DrDxt.Detection.Latency;

// ============================================================================================
// QUEL PILOTE COUTE DES MICRO-SACCADES SUR CETTE MACHINE.
//
// Une liste de reglages n'est pas un diagnostic. Un pic DPC ne coute pas des FPS moyens : il
// coute UNE TRAME. A 144 Hz une image dure 6,9 ms ; un DPC de 8 ms la fait rater a lui seul,
// et le joueur le sent sans savoir le nommer.
//
// Ce que cet outil rend est SPECIFIQUE a cette machine et NOMME le fautif. C'est ce qu'aucun
// article de reglages ne peut faire.
//
// LECTURE PURE : une session ETW noyau privee, arretee dans tous les cas. Rien n'est applique.
//
//   dotnet run --project tools/dpcscan -- [secondes]      (ELEVATION REQUISE)
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var secondes = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 10.0;

Console.WriteLine("== DPC / ISR — ce que les pilotes de cette machine coutent ==");
Console.WriteLine($"   observation : {secondes:0.#} s, lecture pure");
Console.WriteLine();

var trace = new DpcLatencyTracer().Observer(TimeSpan.FromSeconds(secondes));

if (trace.Issue != DpcOutcome.Ok)
{
    Console.WriteLine($"   trace indisponible : {trace.Issue}"
                      + (trace.CodeWin32 != 0 ? $" (code Win32 {trace.CodeWin32})" : ""));
    Console.WriteLine($"   evenements bruts recus : {trace.EvenementsTotal}");
    if (trace.Issue == DpcOutcome.DroitsInsuffisants)
        Console.WriteLine("   → une session noyau exige l'elevation. Relancer en administrateur.");
    if (trace.Issue == DpcOutcome.AucunEvenement)
        Console.WriteLine("   → session ouverte mais muette : une autre trace noyau (xperf, WPR)"
                          + " tient peut-etre la place.");
    Console.WriteLine("   Aucun chiffre n'est devine a la place.");
    return 1;
}

Console.WriteLine($"== CE QUI COUTE ({trace.DureeObserveeSecondes:0.#} s observees, "
                  + $"{trace.EvenementsTotal} evenements bruts) ==");
Console.WriteLine();
Console.WriteLine($"   {"pilote",-24}{"pire",11}{"total",11}{"nb",9}{"> 1 ms",9}");
Console.WriteLine($"   {new string('-', 60)}");

foreach (var c in trace.Couts.Take(12))
    Console.WriteLine($"   {c.Module,-24}{c.MaxMs,8:0.000} ms{c.TotalMs,8:0.0} ms"
                      + $"{c.Evenements,9}{c.AuDelaDeUneMs,9}");

Console.WriteLine();
Console.WriteLine($"   temps en DPC/ISR : l'equivalent de {trace.EquivalentCoeurPercent:0.0} % d'UN cœur"
                  + $"  ({trace.PartMachinePercent:0.00} % de la machine, {Environment.ProcessorCount} processeurs)");
if (trace.RejetesDeltaInvalide > 0 || trace.RejetesTropCourts > 0)
    Console.WriteLine($"   evenements NON decodes : {trace.RejetesDeltaInvalide} durees invalides,"
                      + $" {trace.RejetesTropCourts} charges trop courtes — la disposition a change.");
Console.WriteLine($"   opcodes vus : "
                  + string.Join("  ", trace.ParOpcode.OrderBy(k => k.Key)
                      .Select(kv => $"{Nommer(kv.Key)}={kv.Value}")));
Console.WriteLine();

// ── CE QUE CE CHIFFRE NE PROUVE PAS ─────────────────────────────────────────────────────
// Un pic DPC mesure AU REPOS n'est pas le pic qu'on subit EN JEU : la charge change les
// pilotes qui parlent. Ce banc dit ou regarder, il ne dit pas encore ce que ca coute en FPS.
var pire = trace.Couts.FirstOrDefault();
if (pire is not null && pire.MaxMs >= 1.0)
{
    Console.WriteLine($"   {pire.Module} a pris {pire.MaxMs:0.000} ms d'un coup — au-dela d'une"
                      + " trame a 144 Hz (6,9 ms) c'est une saccade certaine, en dessous c'est un");
    Console.WriteLine("      risque quand la trame est deja pleine.");
}
else
{
    Console.WriteLine("   Aucun DPC au-dela de 1 ms sur cette fenetre. Ce n'est pas « la machine est");
    Console.WriteLine("   saine » : c'est « pas pendant CES secondes-la, a CETTE charge ».");
}

Console.WriteLine("   Un releve ne vaut que pour la charge pendant laquelle il a ete pris.");
Console.WriteLine("      09/09 : trois releves sous 0,4 ms ; 11/09 : Wdf01000.sys a 5,8 ms. Un releve");
Console.WriteLine("      propre n'exclut pas le pic suivant.");
if (pire?.Module.Equals("Wdf01000.sys", StringComparison.OrdinalIgnoreCase) == true)
    Console.WriteLine("   Wdf01000.sys est le cadre KMDF : le vrai pilote est derriere lui.");
return 0;

static string Nommer(byte opcode) => opcode switch
{
    66 => "ThreadedDPC",
    67 => "ISR",
    68 => "DPC",
    69 => "TimerDPC",
    _ => opcode.ToString(),
};
