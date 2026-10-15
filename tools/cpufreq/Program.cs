using DrDxt.Detection.Cpu;

// ============================================================================================
// LA FREQUENCE REELLE DU PROCESSEUR, ET CE QUI LA BRIDE — SUIVIE DANS LE TEMPS.
//
// Pourquoi un outil separe de `CpuFrequencyReader` : lu a travers `repairaudit`, il met des minutes
// et sort un audit entier. Une frequence
// se juge SOUS CHARGE, pendant quelques secondes, au moment ou le jeu tourne — pas dans un
// rapport qu'on lit apres coup.
//
// CE QUI COMPTE ICI N'EST PAS LE MAXIMUM, C'EST LE SOUTENU. Un processeur qui monte a 5,4 GHz
// pendant 2 s puis retombe a 4,0 GHz ne rend pas 5,4 GHz a un jeu : il rend 4,0. Et
// `IndicateursDeLimite` dit quel indicateur Windows est leve -- 0 est une valeur mesuree, pas une
// absence de lecture, mais 0 ne veut PAS dire « rien ne bride » : ce compteur ne voit pas les
// limites de puissance du paquet (RAPL). Mesure sur cette machine : 0 pendant que la frequence
// tombait de 5292 a 3698 MHz sous charge soutenue.
//
//   dotnet run --project tools/cpufreq -- [secondes] [intervalleMs]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var secondes = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 20.0;
var intervalle = args.Length > 1 && int.TryParse(args[1], out var i) ? i : 1000;

Console.WriteLine("== FREQUENCE PROCESSEUR — reelle, et ce qui la bride ==");
Console.WriteLine($"   {secondes:0.#} s, un releve toutes les {intervalle} ms, lecture pure");
Console.WriteLine();

var lecteur = new CpuFrequencyReader();
var releves = new List<double>();
var brides = 0;
var masques = new HashSet<double>();
var fin = DateTime.UtcNow.AddSeconds(secondes);

Console.WriteLine("   temps     effective    % nominal   limite atteinte   bridage");
while (DateTime.UtcNow < fin)
{
    var e = lecteur.Lire();
    var mhz = e.FrequenceEffectiveMhz;
    if (mhz is { } m) releves.Add(m);
    if (e.EstBride is true) brides++;
    if (e.IndicateursDeLimite is { } f) masques.Add(f);

    Console.WriteLine($"   {DateTime.Now:HH:mm:ss}   {Fmt(mhz, "MHz"),10}   {Fmt(e.PourcentageDeNominal, "%"),9}"
                      + $"   {Fmt(e.PartDeLaLimite, "%"),15}   "
                      + (e.EstBride is null ? "non lu" : e.EstBride is true ? $"OUI (masque {e.IndicateursDeLimite})" : "aucun"));
    Thread.Sleep(intervalle);
}

Console.WriteLine();
if (releves.Count == 0)
{
    Console.WriteLine("   Aucune frequence lue. Les compteurs de performance n'ont rien rendu —");
    Console.WriteLine("   aucun chiffre n'est devine a la place.");
    return 1;
}

releves.Sort();
var median = releves[releves.Count / 2];
Console.WriteLine($"   soutenu (mediane) : {median:0} MHz     min {releves[0]:0}     max {releves[^1]:0}");
Console.WriteLine($"   bridage observe   : {brides} releve(s) sur {releves.Count}"
                  + (masques.Count > 0 ? $"   masques vus : {string.Join(", ", masques.Order())}" : ""));
Console.WriteLine();
Console.WriteLine("   Une mesure AU REPOS ne dit rien d'une partie : les cœurs inactifs tombent");
Console.WriteLine("      d'eux-memes. Ce releve ne vaut que pendant la charge qu'on veut juger.");
return 0;

static string Fmt(double? v, string unite) => v is { } x ? $"{x:0} {unite}" : "?";
