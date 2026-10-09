using DrDxt.Detection.Gpu;
using DrDxt.GpuNative.Nvml;

var native = new NvmlNative();
Console.WriteLine($"NVML IsAvailable : {native.IsAvailable}");

var verOk = native.TryGetDriverVersion(out var driverVersion);
Console.WriteLine($"Driver branch    : {(verOk ? driverVersion : "(indisponible)")}"
                + $"   [longueur={driverVersion.Length}]");

var readOk = native.TryGetBar1AndFbBytes(0, out var bar1, out var fb);
Console.WriteLine($"Read OK          : {readOk}");
Console.WriteLine($"BAR1 total       : {bar1 / 1024 / 1024} MiB ({bar1} bytes)");
Console.WriteLine($"FB total         : {fb / 1024 / 1024} MiB ({fb} bytes)");

var provider = new NvmlReBarProvider(native);
Console.WriteLine($"ReBAR (provider) : {provider.GetReBarState()}");
Console.WriteLine($"Heuristic direct : {ReBarHeuristic.FromBar1AndFb(bar1, fb)}");

// ── WATTS ET DEGRES ──────────────────────────────────────────────────────────────────────
// `TryGetPowerUsageMilliwatts` et `TryGetTemperatureCelsius` existaient dans l'interface et
// n'avaient AUCUN appelant (verifie le 07/09/2026). Sans eux, aucune phrase sur l'undervolt ne
// peut etre prouvee : « plus froid, moins gourmand » sont des grandeurs, pas des opinions.
//
// Usage : nvml-probe [secondes]   (defaut 5) — LECTURE PURE, rien n'est ecrit.
int secondes = args.Length > 0 && int.TryParse(args[0], out var sArg) && sArg > 0 ? sArg : 5;

Console.WriteLine();
Console.WriteLine($"== watts et degres, {secondes} s d'echantillonnage ==");

var watts = new List<double>();
var degres = new List<double>();
var fin = DateTime.UtcNow.AddSeconds(secondes);
while (DateTime.UtcNow < fin)
{
    if (native.TryGetPowerUsageMilliwatts(0, out var mw)) watts.Add(mw / 1000.0);
    if (native.TryGetTemperatureCelsius(0, out var c)) degres.Add(c);
    Thread.Sleep(200);
}

static void Ligne(string nom, List<double> v, string unite)
{
    if (v.Count == 0) { Console.WriteLine($"   {nom,-12} INDISPONIBLE — NVML n'a rien rendu"); return; }
    Console.WriteLine($"   {nom,-12} moyenne {v.Average(),6:N1} {unite}   min {v.Min(),6:N1}   max {v.Max(),6:N1}   n={v.Count}");
}

Ligne("puissance", watts, "W");
Ligne("temperature", degres, "C");
