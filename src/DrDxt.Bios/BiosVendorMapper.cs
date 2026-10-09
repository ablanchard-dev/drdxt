using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Maps a raw <see cref="MotherboardInfo.Manufacturer"/> string (as WMI reports it,
/// e.g. "ASUSTeK COMPUTER INC.", "Micro-Star International Co., Ltd.") to a
/// <see cref="BiosVendor"/>. Case/space-insensitive; an unrecognized vendor maps to
/// <see cref="BiosVendor.Unknown"/> (which still gets a prudent generic guide).
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
