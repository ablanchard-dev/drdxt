using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.GpuNative.Nvml;

// ─────────────────────────────────────────────────────────────────────────────────────────
// ocstab — LA STABILITE D'UN OFFSET GPU, mesuree au lieu d'etre esperee.
//
// Le produit dit deja, de la memoire : « XMP est un overclock, et nous ne l'avons pas teste ».
// La meme phrase valait pour l'offset GPU, et rien ne la levait. Cet outil la leve, dans des
// bornes qu'il annonce lui-meme.
//
// COMMENT : `GpuBench` fait tourner un noyau de calcul DETERMINISTE et compare le resultat a un
// checksum a REPONSE CONNUE. Un overclock instable ne rend pas « moins vite » : il rend FAUX,
// ou il fait tomber le pilote (TDR). Une passe qui rend le bon checksum est donc une passe qui
// prouve quelque chose -- et il en faut beaucoup, longtemps, et a chaud.
//
// ══ CE QUE CET OUTIL NE PROUVE PAS, et il faut le lire avant de s'en servir ══
//   • Il exerce le CALCUL. Un jeu utilise en plus les unites de texture, le rasteriseur, le
//     ray-tracing et la memoire video autrement -- un offset peut tenir ici et tomber en jeu.
//   • La stabilite est STATISTIQUE : « zero erreur en 20 minutes » ne dit rien de la 21e.
//     Le verdict est donc toujours borne par sa duree, jamais « stable ».
//   • Il ne teste QUE le reglage actuellement applique : il n'ecrit rien, ni offset ni limite.
//
// Usage :  dotnet run -c Release --project tools/ocstab -- [minutes]     (defaut 5)
// ─────────────────────────────────────────────────────────────────────────────────────────

double minutes = args.Length > 0 && double.TryParse(args[0], out var m) && m > 0 ? m : 5.0;
var fin = DateTime.UtcNow.AddMinutes(minutes);

var nvml = new NvmlNative();
var runner = new VorticeGpuComputeRunner();
var spec = GpuBenchWorkload.Default;
var bench = new GpuBench(runner, spec);

Console.WriteLine("== ocstab — stabilite d'un offset GPU, par checksum a reponse connue ==");
Console.WriteLine($"   duree demandee : {minutes:0.##} min   |   checksum attendu : 0x{GpuBenchWorkload.ComputeKnownChecksum(spec):X16}");
Console.WriteLine("   LECTURE SEULE : cet outil n'applique aucun reglage, il teste celui qui est en place.");
Console.WriteLine();

int passes = 0, verifiees = 0;
uint tempMax = 0;
double wattMax = 0;
string? echec = null;

while (DateTime.UtcNow < fin && echec is null)
{
    // ON MESURE PENDANT LA CHARGE, PAS APRES — corrige apres m'etre trompe deux fois.
    // Premiere version : passes de 10 s, releve APRES la passe, pointe annoncee 63 C. J'en ai
    // conclu « la carte refroidit entre les passes » et j'ai rallonge les passes a 60 s. La
    // pointe est alors tombee a 55 C -- l'inverse de ce que la correction promettait. La cause
    // n'etait pas la duree : je lisais la temperature UNE FOIS LA CHARGE FINIE, donc pendant le
    // refroidissement. Un banc concurrent lisait 80 C au meme moment.
    // ⇒ La charge tourne sur un fil, et on echantillonne PENDANT.
    var tache = Task.Run(() => bench.Run(0, TimeSpan.FromSeconds(60), CancellationToken.None));
    while (!tache.IsCompleted)
    {
        if (nvml.TryGetTemperatureCelsius(0, out var cc)) tempMax = Math.Max(tempMax, cc);
        if (nvml.TryGetPowerUsageMilliwatts(0, out var mmw)) wattMax = Math.Max(wattMax, mmw / 1000.0);
        Thread.Sleep(500);
    }

    var res = await tache;
    passes++;

    if (!res.IsOk || res.Value is null)
    {
        echec = $"le banc n'a pas pu tourner ({res.Error?.Code}: {res.Error?.Message})";
        break;
    }

    var r = res.Value;

    // Les deux facons dont un overclock instable se trahit, et aucune n'est « plus lent ».
    if (r.Stability == StabilityOutcome.Unstable)
        echec = "INSTABLE — pilote tombe (TDR) ou checksum FAUX : le calcul a rendu un mauvais resultat";
    else if (!r.ChecksumOk)
        echec = $"CORRUPTION SILENCIEUSE — checksum 0x{r.Checksum:X16} au lieu de la reponse connue";
    else
        verifiees++;

    Console.Write($"\r   passe {passes,3}  verifiees {verifiees,3}  max {tempMax,3} C / {wattMax,6:N1} W   ");
}

Console.WriteLine();
Console.WriteLine();

if (echec is not null)
{
    Console.WriteLine($"VERDICT : ÉCHEC a la passe {passes} apres {verifiees} passes propres.");
    Console.WriteLine($"          {echec}");
    Console.WriteLine("          Ce reglage n'est pas sur. Reviens a l'offset precedent.");
    return 1;
}

Console.WriteLine($"VERDICT : {verifiees} passes sur {passes}, checksum verifie a chaque fois, aucun TDR.");
Console.WriteLine($"          Pointe thermique {tempMax} C, pointe de puissance {wattMax:N1} W.");
Console.WriteLine();
Console.WriteLine($"CE QUE CA DIT : ce reglage a tenu {minutes:0.##} min de calcul deterministe a chaud,");
Console.WriteLine("                sans une seule erreur de resultat.");
Console.WriteLine("CE QUE CA NE DIT PAS : rien sur la 21e minute, rien sur les unites qu'un JEU");
Console.WriteLine("                utilise en plus (textures, rasteriseur, ray-tracing, memoire video).");
Console.WriteLine("                « Aucune erreur pendant N minutes » n'est pas « stable ».");
return 0;
