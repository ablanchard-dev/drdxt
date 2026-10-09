using System.Diagnostics;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.GpuNative.Nvml;

// ============================================================================
// PREMIERE TRANCHE VERTICALE ROOT-CAUSE — « a CET instant, que faisait la machine ? »
//
// Une charge GPU REELLE tourne pendant que la telemetrie est echantillonnee, et les deux
// sont fusionnees sur une base de temps commune. C'est le contraire de ce que DrDXT
// faisait jusqu'ici : correler « la frame 872 avec la valeur 872 », par index — ce qui
// interdit de rapprocher deux producteurs qui n'echantillonnent pas au meme rythme.
//
// CE QUE CETTE TRANCHE N'A PAS ENCORE : les frametimes d'un vrai jeu (il faut la paire
// PresentMon assortie, gate ouvert) et les DPC/ISR (ETW, lot suivant). La timeline est
// donc CREUSE — et c'est exactement ce que sa conception prevoit : `null` = pas mesure,
// jamais une valeur inventee. La couverture est affichee pour que personne ne prenne une
// correlation calculee sur 4 % des points pour un fait.
//
//   dotnet run --project tools/timeline -- [secondes]
// ============================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;
var secondes = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 8.0;
const int Gpu = 0;

Console.WriteLine("== TIMELINE ROOT-CAUSE — charge GPU reelle + telemetrie synchronisee ==");

var nvml = new NvmlNative();
if (!nvml.TryGetDriverVersion(out var pilote))
{
    Console.WriteLine("   NVML indisponible : aucune telemetrie a fusionner.");
    return 2;
}
Console.WriteLine($"   pilote NVIDIA : {pilote}   duree : {secondes:0.#} s");
Console.WriteLine();

// ── LA BASE DE TEMPS COMMUNE ────────────────────────────────────────────────────────────
// `Stopwatch` s'appuie sur QPC : monotone, de haute resolution, partage par tous les
// producteurs de CE processus. C'est ce qui permet de fusionner deux cadences differentes.
var horloge = Stopwatch.StartNew();
long MaintenantNs() => (long)(horloge.Elapsed.TotalMilliseconds * 1_000_000);

// ── PRODUCTEUR 2 : la telemetrie, echantillonnee PENDANT la charge ──────────────────────
// Mesure du 07/09/2026 : echantillonner APRES la charge donnait 63 °C au lieu de 80 °C
// reels — la carte refroidit en une poignee de secondes. Un capteur lu au mauvais instant
// ne mesure pas la meme machine.
var telemetrie = new List<TimelineSample>();
using var arret = new CancellationTokenSource();
var echantillonneur = Task.Run(() =>
{
    while (!arret.Token.IsCancellationRequested)
    {
        double? watts = nvml.TryGetPowerUsageMilliwatts(Gpu, out var mw) ? mw / 1000.0 : null;
        double? degres = nvml.TryGetTemperatureCelsius(Gpu, out var c) ? c : null;
        if (watts is not null || degres is not null)
            telemetrie.Add(new TimelineSample(MaintenantNs(), GpuPowerW: watts, GpuTemperatureC: degres));
        Thread.Sleep(50);
    }
});

// ── PRODUCTEUR 1 : la charge, decoupee en tranches horodatees ───────────────────────────
var bench = new GpuBench(new VorticeGpuComputeRunner(), GpuBenchWorkload.Default);
var reference = new List<TimelineSample>();
var fin = horloge.Elapsed.TotalSeconds + secondes;
while (horloge.Elapsed.TotalSeconds < fin)
{
    var debutNs = MaintenantNs();
    var r = bench.Run(Gpu, TimeSpan.FromSeconds(1), CancellationToken.None);
    if (r.IsOk && r.Value is { } v && v.ChecksumOk)
        // Le « frametime » d'un banc est le temps par iteration : la grandeur qui ralentit
        // quand la carte ralentit. C'est l'analogue de la frame, pas la frame elle-meme.
        reference.Add(new TimelineSample(debutNs + (long)(v.ElapsedMilliseconds * 500_000),
            FrameTimeMs: v.ElapsedMilliseconds / Math.Max(1, v.IterationsCompleted),
            GpuUtilizationPct: 100));
}

arret.Cancel();
await echantillonneur;

// ── LA FUSION ───────────────────────────────────────────────────────────────────────────
var fusion = PerformanceTimeline.Fusionner(reference, telemetrie);
var couvW = PerformanceTimeline.Couverture(fusion, x => x.GpuPowerW);
var couvT = PerformanceTimeline.Couverture(fusion, x => x.GpuTemperatureC);

Console.WriteLine($"   producteur 1 (charge)     : {reference.Count} points");
Console.WriteLine($"   producteur 2 (telemetrie) : {telemetrie.Count} points");
Console.WriteLine($"   couverture apres fusion   : watts {couvW:P0}   temperature {couvT:P0}");
Console.WriteLine();

Console.WriteLine("   t (ms)   ms/iter    watts    °C     lecture prudente");
foreach (var x in fusion)
{
    var w = x.GpuPowerW;
    var c = x.GpuTemperatureC;

    // CLASSIFICATION PRUDENTE. « coincide avec » n'est pas « est cause par ». Sans la
    // limite de puissance declaree par la carte ni sa frequence, on ne peut QUE decrire ce
    // qu'on voit — et c'est deja plus que ce que le produit savait dire.
    var lecture = (w, c) switch
    {
        (null, null) => "aucune telemetrie a cet instant",
        ( > 110, > 75) => "charge soutenue, carte chaude — limite possible, non prouvee",
        ( > 110, _) => "charge soutenue",
        ( < 60, _) => "carte peu sollicitee a cet instant",
        _ => "regime intermediaire",
    };

    Console.WriteLine($"   {x.TimestampNs / 1_000_000,6}   {x.FrameTimeMs,7:0.0000}   "
                      + $"{(w is null ? "   -" : w.Value.ToString("0.0")),5}   "
                      + $"{(c is null ? "  -" : c.Value.ToString("0")),3}     {lecture}");
}

Console.WriteLine();
Console.WriteLine("== CE QUE CETTE TRANCHE ETABLIT ==");
Console.WriteLine("   Deux producteurs de cadences differentes ont ete fusionnes sur une base de temps");
Console.WriteLine("   commune, et chaque point sait s'il a une telemetrie ou non. Ce qui manque est");
Console.WriteLine("   NOMME : les frametimes d'un vrai jeu (paire PresentMon assortie, gate ouvert) et");
Console.WriteLine("   les DPC/ISR (ETW). La timeline est creuse, elle n'invente rien.");

return fusion.Count > 0 && couvW > 0 ? 0 : 1;
