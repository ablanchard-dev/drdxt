using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Resolves the BIOS-UI generation token used to key the guide library, from the
/// vendor + <see cref="BiosInfo"/>. Each vendor names its recent BIOS UI distinctly
/// (MSI "Click BIOS 5", ASUS "AI Tweaker UEFI", Gigabyte "Aorus UEFI", ASRock "UEFI").
///
/// HONESTY: this is a coarse heuristic (the per-revision mapping is bench data we do
/// not have yet — plan §16 / §5 trou n°2). When we cannot confidently pin a
/// generation we return the vendor's "recent" token, and the library degrades to a
/// labelled fallback if that exact entry is not curated. It never throws and always
/// returns a non-blank token, so the library can always resolve something usable.
/// </summary>
public static class BiosUiGenerationDetector
{
    /// <summary>The generic, vendor-agnostic generation token (always a valid library key).</summary>
    public const string Generic = "Generic";

    public static string Detect(BiosVendor vendor, BiosInfo bios)
    {
        _ = bios; // reserved for future version/date-based discrimination (bench data)
        return vendor switch
        {
            BiosVendor.Msi => "Click BIOS 5",
            BiosVendor.Asus => "AI Tweaker UEFI",
            BiosVendor.Gigabyte => "Aorus UEFI",
            BiosVendor.Asrock => "ASRock UEFI",
            _ => Generic,
        };
    }
}
