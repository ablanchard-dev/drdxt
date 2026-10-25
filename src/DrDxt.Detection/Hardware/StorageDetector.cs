using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Détecte les disques physiques via MSFT_PhysicalDisk (root\Microsoft\Windows\Storage),
/// avec repli sur Win32_DiskDrive (root\cimv2). Pour l'UI seulement : non noté.
/// Frontière WMI complète : jamais d'exception, non-admin.
/// </summary>
public sealed class StorageDetector : IStorageDetector
{
    // MSFT_PhysicalDisk MediaType values
    private const int MediaTypeHdd = 3;
    private const int MediaTypeSsd = 4;

    // MSFT_PhysicalDisk BusType values
    private const int BusTypeNvme = 17;

    // USB (7), SD (12), MMC (13). Windows peut lister une clé avant le disque système, et l'écran prend
    // le premier disque : les amovibles passent après les internes.
    private static readonly int[] BusAmovibles = [7, 12, 13];

    private readonly IWmiQuery _wmi;

    public StorageDetector(IWmiQuery wmi) => _wmi = wmi;

    public Result<IReadOnlyList<StorageDevice>> Detect()
    {
        var primary = TryMsftPhysicalDisk();
        if (primary.IsOk && primary.Value!.Count > 0)
            return primary;

        return TryWin32DiskDrive();
    }

    private Result<IReadOnlyList<StorageDevice>> TryMsftPhysicalDisk()
    {
        var result = _wmi.QueryInNamespace(
            // Avec ou sans le prefixe `\\.\`, la requete aboutit (mesure du 25/10/2026). Si elle echouait, le
            // `TryWin32DiskDrive()` juste en dessous la rattraperait en silence : le produit tournerait sur le
            // lecteur degrade sans que rien ne le signale.
            @"root\Microsoft\Windows\Storage",
            "SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");

        if (!result.IsOk)
            return Result<IReadOnlyList<StorageDevice>>.Fail(ErrorCodes.StorageFail, result.Error!.Value.Message);

        var devices = new List<(StorageDevice Disque, bool Amovible)>();
        foreach (var row in result.Value!)
        {
            var model = row.GetString("FriendlyName") ?? "Unknown";
            var mediaType = row.GetInt("MediaType");
            var busType = row.GetInt("BusType");
            var sizeBytes = (long)(row.GetUlong("Size") ?? 0UL);

            string kind = "Unknown";
            if (busType == BusTypeNvme)
                kind = "NVMe";
            else if (mediaType == MediaTypeSsd)
                kind = "SSD";
            else if (mediaType == MediaTypeHdd)
                kind = "HDD";

            devices.Add((new StorageDevice(model, kind, sizeBytes), busType is { } bus && BusAmovibles.Contains(bus)));
        }
        return Result<IReadOnlyList<StorageDevice>>.Ok(devices.OrderBy(d => d.Amovible).Select(d => d.Disque).ToList());
    }

    private Result<IReadOnlyList<StorageDevice>> TryWin32DiskDrive()
    {
        var result = _wmi.Query(
            "SELECT Model, Size, MediaType FROM Win32_DiskDrive");

        if (!result.IsOk)
            return Result<IReadOnlyList<StorageDevice>>.Fail(ErrorCodes.StorageFail, result.Error!.Value.Message);

        var devices = new List<(StorageDevice Disque, bool Amovible)>();
        foreach (var row in result.Value!)
        {
            var model = row.GetString("Model") ?? "Unknown";
            var sizeBytes = (long)(row.GetUlong("Size") ?? 0UL);
            var mediaTypeStr = row.GetString("MediaType") ?? string.Empty;

            // « Fixed hard disk media » décrit tout disque interne, SSD et NVMe compris : le support n'est pas lu ici.
            // « Removable Media » et « External hard disk media » passent après les internes, comme les bus amovibles.
            var amovible = mediaTypeStr.Contains("Removable", StringComparison.OrdinalIgnoreCase)
                || mediaTypeStr.Contains("External", StringComparison.OrdinalIgnoreCase);
            devices.Add((new StorageDevice(model, "Unknown", sizeBytes), amovible));
        }
        return Result<IReadOnlyList<StorageDevice>>.Ok(devices.OrderBy(d => d.Amovible).Select(d => d.Disque).ToList());
    }
}
