using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

public sealed class ChassisDetector
{
    // SMBIOS System Enclosure types (spec Table 3). Handheld = 11 (Hand Held), 30 (Tablet).
    private static readonly int[] HandheldCodes = { 11, 30 };
    private static readonly int[] DesktopCodes = { 3, 4, 5, 6, 7, 15, 16, 17, 23, 24 };
    private static readonly int[] LaptopCodes = { 8, 9, 10, 14, 18, 21, 31, 32 };
    // Beaucoup de handhelds rapportent un code laptop (ex. 10/Notebook), donc on raffine aussi par modèle.
    private static readonly string[] HandheldModels = { "ROG Ally", "Jupiter", "Legion Go", "Steam Deck", "AOKZOE", "AYANEO", "OneXPlayer", "Claw", "GPD", "Xbox Ally" };

    private readonly IWmiQuery _wmi;
    public ChassisDetector(IWmiQuery wmi) => _wmi = wmi;

    public Result<ChassisType> Detect()
    {
        var model = ReadModel();
        if (HandheldModels.Any(h => model.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return Result<ChassisType>.Ok(ChassisType.Handheld);

        var q = _wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        if (!q.IsOk) return Result<ChassisType>.Fail(q.Error!.Value.Code, q.Error.Value.Message);

        var code = ExtractFirstCode(q.Value!.FirstOrDefault());
        var type = HandheldCodes.Contains(code) ? ChassisType.Handheld
                 : DesktopCodes.Contains(code) ? ChassisType.Desktop
                 : LaptopCodes.Contains(code) ? ChassisType.Laptop
                 : ChassisType.Unknown;
        return Result<ChassisType>.Ok(type);
    }

    private string ReadModel()
    {
        var q = _wmi.Query("SELECT Model FROM Win32_ComputerSystem");
        return q.IsOk ? q.Value!.FirstOrDefault()?.GetString("Model") ?? "" : "";
    }

    private static int ExtractFirstCode(WmiRow? row)
    {
        if (row is null || !row.Properties.TryGetValue("ChassisTypes", out var v) || v is null) return 0;
        return v switch
        {
            ushort[] arr when arr.Length > 0 => arr[0],
            int[] arr when arr.Length > 0 => arr[0],
            System.Collections.IEnumerable e => e.Cast<object>().Select(o => Convert.ToInt32(o)).FirstOrDefault(),
            _ => 0
        };
    }
}
