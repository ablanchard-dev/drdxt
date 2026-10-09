using System.Text.RegularExpressions;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Extracts the XMP/EXPO rated data-rate (MHz) encoded in a DRAM part number.
///
/// WMI does not reliably expose a kit's rated speed. After a BIOS flash resets XMP,
/// <c>Win32_PhysicalMemory.Speed</c> collapses to the JEDEC value (== ConfiguredClockSpeed),
/// so the part number becomes the only honest source of the rating — which is exactly
/// when we most need to flag "XMP got reset".
///
/// Conservative by design (we must never invent a problem): we accept only a run of
/// Exactly four digits whose value lands in a plausible DDR4/DDR5 data-rate range, and
/// we take the highest such match — the headline speed, never a lower timing code
/// (e.g. "6000" over the "3038" in F5-6000J3038...). When nothing plausible is found we
/// return null and the caller keeps the WMI value; we never guess.
///
/// Known gap: vendors that compress the speed (Kingston Fury "KF4 32" = DDR4-3200) have
/// no 4-digit run; those return null and fall back to WMI honestly.
/// </summary>
public static partial class DramPartNumber
{
    // Plausible consumer DRAM data-rates: DDR4 ~2133 up to DDR5 ~8400+. Bounds kept wide
    // but above 3-digit codes / below year-like numbers so a stray value isn't read as a speed.
    private const int MinPlausibleMhz = 2000;
    private const int MaxPlausibleMhz = 9000;

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex FourDigitRun();

    public static int? TryParseRatedMhz(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;

        int? best = null;
        foreach (Match m in FourDigitRun().Matches(partNumber))
        {
            // Accept the match only if it is a standalone 4-digit run: a digit on either
            // side means it belongs to a longer number (serial / die code), not a data-rate.
            int start = m.Index;
            int end = m.Index + m.Length;
            if (start > 0 && char.IsDigit(partNumber[start - 1])) continue;
            if (end < partNumber.Length && char.IsDigit(partNumber[end])) continue;

            int value = int.Parse(m.Value);
            if (value < MinPlausibleMhz || value > MaxPlausibleMhz) continue;
            if (best is null || value > best) best = value;
        }
        return best;
    }

    /// <summary>
    /// Extrait la latence CAS (tCL) encodée dans un part number : le « C18 » de
    /// <c>F4-4000C18-32GTES</c>, le « J3038 » de <c>F5-6000J3038F16GX2</c> (CL30).
    ///
    /// Pourquoi c'est la seule source : aucune API Windows documentée n'expose les timings
    /// effectifs — ni <c>Win32_PhysicalMemory</c>, ni le SMBIOS Type 17 (vérifié sur la table
    /// complète). Le SPD les porte, mais il faut le bus SMBus, donc un pilote noyau, donc la
    /// voie BYOVD que le produit refuse.
    ///
    /// Conséquence à ne jamais perdre : ce tCL est celui du KIT, pas celui de la machine.
    /// Si la carte a appliqué un autre timing, il est faux. Tout affichage qui l'utilise doit
    /// dire « la latence du kit à la fréquence mesurée », jamais « ta latence actuelle ».
    ///
    /// Conservateur comme le reste du fichier : on n'accepte que les formes reconnues, et on
    /// rend <c>null</c> plutôt que de deviner. Un CL inventé produirait une latence inventée.
    /// </summary>
    public static int? TryParseCasLatency(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;

        // Forme G.Skill DDR4 : ...-<rate>C<CL>-...  (C18, C16, C14)
        var c = System.Text.RegularExpressions.Regex.Match(partNumber, @"\d{4}C(\d{2})(?!\d)");
        if (c.Success && Plausible(c.Groups[1].Value, out int cl)) return cl;

        // Forme G.Skill DDR5 : ...-<rate>J<CL><tRCD><tRP>...  (J3038 => CL30)
        var j = System.Text.RegularExpressions.Regex.Match(partNumber, @"\d{4}J(\d{2})\d{2}");
        if (j.Success && Plausible(j.Groups[1].Value, out int clj)) return clj;

        return null;

        // Bande de plausibilité : CL14 (DDR4 haut de gamme) à CL46 (DDR5 JEDEC). Hors bande,
        // on a matché autre chose qu'un timing — on préfère ne rien rendre.
        static bool Plausible(string raw, out int value)
            => int.TryParse(raw, out value) && value >= 14 && value <= 46;
    }
}
