using DrDxt.Detection.Display;

// Live smoke for the display seam. Prints what Win32DisplayInfo actually reads on this machine.
// Read-only. Must run from a real desktop session — a service has no window station and would
// see nothing, which the seam reports as unavailable rather than as "no display".
//
//   dotnet run --project tools/dispprobe

Console.WriteLine("== ecrans lus par DrDXT (lecture seule) ==");

var nvapi = new DrDxt.GpuNative.NvApi.NvApiNative();
var seam = new Win32DisplayInfo(nvapi);

// Recoupement du VRR par un SECOND chemin du pilote : le reglage « Enable G-SYNC globally » de la
// base DRS. Source : NVIDIA NvApiDriverSettings.h — VRR_MODE_ID = 0x1194F158 ; 0 desactive,
// 1 plein ecran seul (DEFAUT), 2 plein ecran et fenetre. Lecture seule, non elevee.
Console.WriteLine(nvapi.TryReadDrsGlobalSetting(0x1194F158u, out var vrrMode)
    ? $"== G-SYNC global (DRS VRR_MODE) : {vrrMode.Value} (0 off · 1 plein ecran · 2 plein ecran+fenetre), emplacement={vrrMode.SettingLocation}"
    : "== G-SYNC global (DRS VRR_MODE) : non lu (absent du profil de base, ou pilote illisible)");
var all = seam.GetAllDisplays();

if (!all.IsOk)
{
    Console.WriteLine($"REFUSE : {all.Error?.Code} — {all.Error?.Message}");
    Console.WriteLine("   (lance-le depuis ta session Windows, pas depuis un service)");
    return 1;
}

var displays = all.Value!;
foreach (var d in displays)
{
    var role = d.IsPrimaryGameDisplay ? "PRINCIPAL " : "secondaire";
    var max = d.MaxRefreshHz > 0 ? $"{d.MaxRefreshHz} Hz" : "non lu";
    Console.WriteLine($"   {role} {d.DeviceName,-14} actuel={d.RefreshHz,4} Hz   max={max,-8}");
    Console.WriteLine($"      VRR (lu au pilote) : possible={d.VrrPossible}  actif={d.VrrEnabled}  => SupportsVrr={d.SupportsVrr}");

    if (d.IsBelowCapability)
        Console.WriteLine($"      -> SOUS SA CAPACITE : {d.RefreshHeadroomHz} Hz inutilises. " +
                          $"Passer cet ecran a {d.MaxRefreshHz} Hz (Affichage avance).");
}

// The two-screen question: is the game running on the slower panel?
var primary = displays.FirstOrDefault(d => d.IsPrimaryGameDisplay);
var fastest = displays.OrderByDescending(d => d.RefreshHz).First();
if (primary is not null && !ReferenceEquals(primary, fastest) && fastest.RefreshHz > primary.RefreshHz)
    Console.WriteLine($"   -> l'ecran PRINCIPAL ({primary.RefreshHz} Hz) n'est pas le plus rapide " +
                      $"({fastest.DeviceName} a {fastest.RefreshHz} Hz).");

// What is NOT read here, said out loud rather than left to be assumed.
Console.WriteLine();
Console.WriteLine("   VRR : lu au pilote NVIDIA (NVAPI) ; Unknown = non lu (AMD, autre carte), jamais « non ».");
Console.WriteLine("   le max est cherche A LA RESOLUTION COURANTE, pas toutes resolutions");
Console.WriteLine("            confondues — sinon un mode 800x600 passerait pour la capacite de l'ecran.");

return 0;
