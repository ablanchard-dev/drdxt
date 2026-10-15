using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Relie une chaîne brute <see cref="MotherboardInfo.Manufacturer"/> (telle que WMI la rapporte,
/// par exemple « ASUSTeK COMPUTER INC. », « Micro-Star International Co., Ltd. ») à un
/// <see cref="BiosVendor"/>. Sans tenir compte de la casse ni des espaces ; un constructeur non
/// reconnu donne <see cref="BiosVendor.Unknown"/> (qui reçoit quand même un guide générique prudent).
/// </summary>
public static class BiosVendorMapper
{
    public static BiosVendor FromManufacturer(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
            return BiosVendor.Unknown;

        var m = manufacturer.Trim();

        if (Contains(m, "ASUS") || Contains(m, "ASUSTeK"))
            return BiosVendor.Asus;
        if (Contains(m, "MSI") || Contains(m, "Micro-Star") || Contains(m, "Micro Star"))
            return BiosVendor.Msi;
        if (Contains(m, "Gigabyte") || Contains(m, "Aorus"))
            return BiosVendor.Gigabyte;
        if (Contains(m, "ASRock"))
            return BiosVendor.Asrock;

        return BiosVendor.Unknown;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
