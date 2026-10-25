using System.Text.RegularExpressions;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Extrait le débit nominal XMP/EXPO (MHz) encodé dans le part number d'une barrette.
///
/// WMI n'expose pas de façon fiable la vitesse nominale d'un kit. Après un flash du BIOS qui
/// remet XMP à zéro, <c>Win32_PhysicalMemory.Speed</c> retombe sur la valeur JEDEC
/// (== ConfiguredClockSpeed) : le part number devient la seule source honnête du nominal, et
/// c'est justement là qu'il faut signaler « XMP a été remis à zéro ».
///
/// Prudent par conception (on n'invente jamais un problème) : on n'accepte qu'une suite
/// d'exactement quatre chiffres dont la valeur tombe dans une plage plausible de débit
/// DDR4/DDR5, et on prend la plus haute : la vitesse affichée, jamais un code de timing plus bas
/// (par exemple « 6000 » plutôt que le « 3038 » de F5-6000J3038...). Rien de plausible : on rend
/// null et l'appelant garde la valeur WMI ; on ne devine jamais.
///
/// Limite connue : les fabricants qui compressent la vitesse (Kingston Fury « KF4 32 » =
/// DDR4-3200) n'ont pas de suite de 4 chiffres ; ils rendent null et retombent honnêtement sur WMI.
/// Crucial (« CT… ») code la sienne autrement (CT16G4DFD8266 = DDR4-2666) : sa suite de 4 chiffres n'est pas un débit,
/// et sa référence rend null elle aussi.
/// </summary>
public static partial class DramPartNumber
{
    // Débits plausibles de la mémoire grand public : DDR4 ~2133 jusqu'à DDR5 ~8400+. Bornes
    // larges, au-dessus des codes à 3 chiffres. Une suite qui ressemble à une année (2000 à 2099) y entre : elle ne
    // l'emporte que si elle dépasse le débit lu par WMI.
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
            // N'accepter la correspondance que si c'est une suite isolée de 4 chiffres : un chiffre de
            // part ou d'autre veut dire qu'elle appartient à un nombre plus long (série, code de puce).
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
    /// effectifs : ni <c>Win32_PhysicalMemory</c>, ni le SMBIOS Type 17 (vérifié sur la table
    /// complète). Le SPD les porte, mais il faut le bus SMBus, donc un pilote noyau, donc la
    /// voie BYOVD que le produit refuse.
    ///
    /// Conséquence à ne jamais perdre : ce tCL est celui du kit, pas celui de la machine.
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
        // on a matché autre chose qu'un timing : on préfère ne rien rendre.
        static bool Plausible(string raw, out int value)
            => int.TryParse(raw, out value) && value >= 14 && value <= 46;
    }
}
