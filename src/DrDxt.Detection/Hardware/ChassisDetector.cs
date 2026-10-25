using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

public sealed class ChassisDetector
{
    // Types SMBIOS System Enclosure (table 3 de la spec). Console portable = 11 (Hand Held), 30 (Tablet).
    private static readonly int[] HandheldCodes = { 11, 30 };
    private static readonly int[] DesktopCodes = { 3, 4, 5, 6, 7, 15, 16, 17, 23, 24 };
    // 18 (Expansion Chassis) et 21 (Peripheral Chassis) ne sont pas des portables : inconnus, ils se rangent par la batterie.
    private static readonly int[] LaptopCodes = { 8, 9, 10, 14, 31, 32 };
    // Beaucoup de handhelds rapportent un code laptop (ex. 10/Notebook), donc on raffine aussi par modèle. Le Steam Deck
    // se nomme « Jupiter » (LCD) ou « Galileo » (OLED).
    private static readonly string[] HandheldModels = { "ROG Ally", "Jupiter", "Legion Go", "Steam Deck", "Galileo", "AOKZOE", "AYANEO", "OneXPlayer", "Claw", "GPD", "Xbox Ally" };

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
        // Mini PC (35), tout-en-un (13) et beaucoup de cartes mères de PC montés (« Other » 1,
        // « Unknown » 2) tombaient en « inconnu » et perdaient les leviers de bureau sans explication. Sans batterie,
        // ce n'est pas un portable : il est traité en PC fixe. Une station d'accueil (12) a la batterie de son portable.
        if (type == ChassisType.Unknown && SansBatterie()) type = ChassisType.Desktop;
        return Result<ChassisType>.Ok(type);
    }

    private bool SansBatterie()
    {
        var q = _wmi.Query("SELECT Name FROM Win32_Battery");
        return q.IsOk && q.Value is { Count: 0 };
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
