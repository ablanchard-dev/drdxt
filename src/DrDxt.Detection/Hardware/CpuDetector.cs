using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

public sealed class CpuDetector
{
    private readonly IWmiQuery _wmi;
    public CpuDetector(IWmiQuery wmi) => _wmi = wmi;

    public Result<CpuInfo> Detect()
    {
        var q = _wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        if (!q.IsOk) return Result<CpuInfo>.Fail(q.Error!.Value.Code, q.Error.Value.Message);

        // V1 gaming scope: un seul CPU. Sur un système multi-socket on prend la 1ère
        // ligne Win32_Processor (une par socket physique). Limite connue : un système à plusieurs sockets n'est pas agrégé.
        var row = q.Value!.FirstOrDefault();
        if (row is null)
            return Result<CpuInfo>.Fail(ErrorCodes.WmiFail, "Win32_Processor returned no rows.");

        var model = row.GetString("Name") ?? "Unknown CPU";
        var vendor = row.GetString("Manufacturer") ?? "Unknown";
        var cores = row.GetInt("NumberOfCores") ?? 0;
        var threads = row.GetInt("NumberOfLogicalProcessors") ?? cores;

        var isX3d = model.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        var ccd = EstimateCcdCount(vendor, cores);

        return Result<CpuInfo>.Ok(new CpuInfo(model, vendor, cores, threads, isX3d, ccd));
    }

    // Heuristique CCD (Ryzen Zen) : un CCD = jusqu'à 8 cœurs. Détection par le fabricant
    // (Win32_Processor.Manufacturer = "AuthenticAMD"/"GenuineIntel"), jamais par le nom
    // de modèle (fragile, chaînes OEM variables). Tout non-AMD → non applicable (0).
    private static int EstimateCcdCount(string vendor, int cores)
    {
        var isAmd = vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || vendor.Contains("Authentic", StringComparison.OrdinalIgnoreCase);
        if (!isAmd || cores <= 0) return 0;
        return (int)Math.Ceiling(cores / 8.0);
    }
}
