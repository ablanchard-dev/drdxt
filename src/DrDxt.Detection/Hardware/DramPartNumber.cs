using System.Text.RegularExpressions;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Extrait le débit nominal XMP/EXPO (MHz) encodé dans le part number d'une barrette. Après un flash du BIOS qui remet
/// XMP à zéro, <c>Win32_PhysicalMemory.Speed</c> retombe sur le JEDEC (== ConfiguredClockSpeed) : le part number est alors
/// la seule source du nominal, au moment précis où il faut dire « XMP a été remis à zéro ».
/// Seulement une suite isolée de quatre chiffres dans la plage DDR4/DDR5, la plus haute (« 6000 », pas le « 3038 » de
/// F5-6000J3038...) ; rien de plausible : null, l'appelant garde WMI. Limite connue : Kingston Fury (« KF4 32 » =
/// DDR4-3200) et Crucial (CT16G4DFD8266 = DDR4-2666) codent la vitesse autrement et rendent null.
/// </summary>
public static partial class DramPartNumber
{
    // DDR4 ~2133 à DDR5 ~8400+, au-dessus des codes à 3 chiffres. Une suite qui ressemble à une année (2000 à 2099) y
    // entre : elle ne l'emporte que si elle dépasse le débit lu par WMI.
    private const int MinPlausibleMhz = 2000;
    private const int MaxPlausibleMhz = 9000;

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex FourDigitRun();

    public static int? TryParseRatedMhz(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;
        if (partNumber.TrimStart().StartsWith("CT", StringComparison.OrdinalIgnoreCase)) return null;

        int? best = null;
        foreach (Match m in FourDigitRun().Matches(partNumber))
        {
            // Isolée : un chiffre voisin veut dire un nombre plus long (série, code de puce).
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
    /// Extrait la latence CAS (tCL) d'un part number : le « C18 » de <c>F4-4000C18-32GTES</c>, le « J3038 » de
    /// <c>F5-6000J3038F16GX2</c> (CL30). Seule source : aucune API Windows documentée n'expose les timings effectifs (ni
    /// <c>Win32_PhysicalMemory</c>, ni le SMBIOS Type 17, vérifié sur la table complète) ; le SPD exige le SMBus, donc un
    /// pilote noyau, la voie BYOVD que le produit refuse. Ce tCL est celui du kit : l'afficher dit « la latence du kit à la
    /// fréquence mesurée », jamais « ta latence actuelle ». Formes reconnues seulement, sinon <c>null</c>.
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

        // CL14 (DDR4 haut de gamme) à CL46 (DDR5 JEDEC) ; hors bande, ce n'est pas un timing.
        static bool Plausible(string raw, out int value)
            => int.TryParse(raw, out value) && value >= 14 && value <= 46;
    }
}
