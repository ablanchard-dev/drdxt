using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.GpuNative.Nvml;

// ============================================================================================
// LA SONDE NVML LIT-ELLE REELLEMENT CETTE CARTE ?
//
// Compiler ne prouve rien, et « l'API existe dans la doc » non plus. `nvmlDeviceGetClockInfo`
// et `nvmlDeviceGetCurrentClocksEventReasons` peuvent parfaitement rendre NVML_ERROR_NOT_SUPPORTED
// sur une carte grand public — c'est deja le cas de la lecture instantanee de puissance sur Ada.
//
// Cet outil lit dix fois de suite et affiche ce qui SORT. Une valeur absente s'affiche « non lu »,
// jamais zero.
//
//   dotnet run --project tools/nvmlprobe
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var nvml = new NvmlNative();
Console.WriteLine("== SONDE NVML — LECTURE REELLE ==");
Console.WriteLine($"   nvml.dll disponible : {(nvml.IsAvailable ? "OUI" : "NON")}");
if (nvml.TryGetDriverVersion(out var pilote)) Console.WriteLine($"   pilote              : {pilote}");
Console.WriteLine(nvml.TryGetPcieLink(0, out var gen, out var largeur, out var genMax, out var largeurMax)
    ? $"   lien PCIe           : Gen{gen} x{largeur} (maximum Gen{genMax} x{largeurMax})"
    : "   lien PCIe           : non lu");
Console.WriteLine(nvml.TryGetMemoryClockMhz(0, out var memMhz, out var memMax)
    ? $"   memoire video       : {memMhz} MHz (maximum {memMax} MHz)"
    : "   memoire video       : non lue");
if (nvml.TryGetGraphicsProcesses(0, out var pids))
{
    Console.WriteLine($"   processus sur la carte : {pids.Length}");
    foreach (var pid in pids)
    {
        string nom;
        try { nom = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { nom = "?"; }
        Console.WriteLine($"      {pid,7}  {nom}");
    }
}
else Console.WriteLine("   processus sur la carte : non lus");
Console.WriteLine();

Console.WriteLine($"   {"MHz",8}{"W",10}{"°C",8}   raisons publiees");
var sonde = new NvmlHardwareProbe(nvml);
var lectures = new List<HardwareReading>();
for (var i = 0; i < 10; i++)
{
    var l = sonde.Lire();
    lectures.Add(l);
    Console.WriteLine($"   {Texte(l.GpuClockMhz, "0"),8}{Texte(l.GpuPowerW, "0.0"),10}"
                      + $"{Texte(l.GpuTemperatureC, "0"),8}   "
                      + (l.Raisons is { } r ? $"0x{(ulong)r:X} {r}" : "non lu"));
    Thread.Sleep(200);
}

var obs = HardwareObservation.De(lectures);
Console.WriteLine();
Console.WriteLine($"   agrege : {obs.Echantillons} echantillons - "
                  + $"{Texte(obs.FrequenceMedianeMhz, "0")} MHz median "
                  + $"(min {Texte(obs.FrequenceMinimaleMhz, "0")}) - "
                  + $"{Texte(obs.PuissanceMedianeW, "0.0")} W - "
                  + $"{Texte(obs.TemperatureMaxC, "0")} °C max");

var verdict = GpuLimiter.Classer(obs.RaisonsVues, null, obs.FrequenceMedianeMhz, obs.FrequenceMinimaleMhz);
Console.WriteLine($"   bridage : {verdict} — {GpuLimiter.Phrase(verdict)}");
Console.WriteLine();

// Le critere de reussite : les QUATRE grandeurs ont ete lues au moins une fois. Une seule
// manquante et le root-cause reste borgne — mieux vaut le savoir maintenant qu'en pleine partie.
var lues = new[]
{
    ("frequence", obs.FrequenceMedianeMhz is not null),
    ("puissance", obs.PuissanceMedianeW is not null),
    ("temperature", obs.TemperatureMaxC is not null),
    ("raisons de bridage", obs.RaisonsVues is not null),
};
foreach (var (nom, ok) in lues)
    Console.WriteLine($"   {(ok ? "OK  " : " ")}{nom}");

return lues.All(x => x.Item2) ? 0 : 1;

static string Texte(double? v, string format) => v?.ToString(format) ?? "non lu";
