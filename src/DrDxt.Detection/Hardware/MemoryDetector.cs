using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

public sealed class MemoryDetector
{
    private readonly IWmiQuery _wmi;
    public MemoryDetector(IWmiQuery wmi) => _wmi = wmi;

    public Result<IReadOnlyList<MemoryModule>> Detect()
    {
        var q = _wmi.Query(
            // Mesure du 26/08 : ces quatre colonnes supplementaires rendent tout ce qu'un
            // parseur SMBIOS maison aurait donne (type, rangs, tension, emplacement), et le
            // recoupement sonde-brute / WMI est exact. Ecrire le parseur aurait ete du travail
            // en plus et un risque d'offset mal lu en plus.
            "SELECT PartNumber, Manufacturer, ConfiguredClockSpeed, Speed, "
            + "SMBIOSMemoryType, Attributes, ConfiguredVoltage, DeviceLocator "
            + "FROM Win32_PhysicalMemory");
        if (!q.IsOk) return Result<IReadOnlyList<MemoryModule>>.Fail(q.Error!.Value.Code, q.Error.Value.Message);

        var modules = q.Value!.Select(r =>
        {
            var partNumber = r.GetString("PartNumber") ?? "Unknown";
            int configured = r.GetInt("ConfiguredClockSpeed") ?? 0;
            int wmiSpeed = r.GetInt("Speed") ?? 0;

            // WMI Speed alone is unreliable for the rated speed: after a BIOS flash resets
            // XMP it collapses to the JEDEC value. Recover the kit's rating from the part
            // number and keep whichever is higher, so an XMP reset stays detectable.
            int rated = Math.Max(wmiSpeed, DramPartNumber.TryParseRatedMhz(partNumber) ?? 0);

            return new MemoryModule(
                partNumber,
                r.GetString("Manufacturer") ?? "Unknown",
                configured,
                rated,
                SmbiosMemoryType: r.GetInt("SMBIOSMemoryType") ?? 0,
                // Attributes porte le nombre de rangs sur ses 4 bits de poids faible.
                Ranks: (r.GetInt("Attributes") ?? 0) & 0x0F,
                ConfiguredVoltageMv: r.GetInt("ConfiguredVoltage") ?? 0,
                DeviceLocator: r.GetString("DeviceLocator"));
        }).ToList();

        return Result<IReadOnlyList<MemoryModule>>.Ok(modules);
    }

    /// <summary>06/10/2026 (chantier B1) : les emplacements de la carte, somme des tableaux de mémoire système
    /// (Use = 3). 0 = non lu : jamais un nombre deviné.</summary>
    public int Emplacements()
    {
        var q = _wmi.Query("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray");
        return q.IsOk ? q.Value!.Where(r => r.GetInt("Use") == 3).Sum(r => r.GetInt("MemoryDevices") ?? 0) : 0;
    }
}
