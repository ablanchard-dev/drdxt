namespace DrDxt.Core;

/// <summary>
/// Le socle JEDEC d'une barrette, ce qu'elle tourne sans profil (inférence, probable : pas une borne certifiée de la
/// spécification). DDR4 : 3200 au plus ; DDR5 : 4800 au moins. Une barrette annonce un profil XMP/EXPO quand sa vitesse
/// nominale (lue, ou décodée de sa référence) dépasse ce socle.
/// </summary>
public static class SocleJedec
{
    // SMBIOS Memory Device Type. 26 = DDR4 (mesuré sur la machine de référence), 34 = DDR5 (valeur de la spécification,
    // non vérifiée ici).
    public const int SmbiosDdr4 = 26;
    public const int SmbiosDdr5 = 34;

    private const int Ddr4Mhz = 3200;
    private const int Ddr5Mhz = 4800;

    /// <summary>Le type lu quand le SMBIOS le donne ; sinon deviné depuis la vitesse nominale (DDR5 JEDEC démarre à
    /// 4800). Lire vaut mieux que deviner : un DDR5-4800 et un DDR4-4800 n'ont pas le même socle.</summary>
    public static int Mhz(MemoryModule m) => m.SmbiosMemoryType switch
    {
        SmbiosDdr5 => Ddr5Mhz,
        SmbiosDdr4 => Ddr4Mhz,
        _ => m.RatedMhz >= Ddr5Mhz ? Ddr5Mhz : Ddr4Mhz,
    };

    public static bool AnnonceUnProfil(MemoryModule m) => m.RatedMhz > Mhz(m);
}
