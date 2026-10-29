using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// La puissance du paquet processeur que Windows publie (« Energy Meter », instance RAPL du paquet), lue par la classe CIM
/// car le nom du compteur est traduit (« Compteur Énergie »). Mesure sur la machine de reference, Warzone en partie :
/// 66,5 W plats sur un i7-14700F de Base Power 65 W, la preuve que la limite longue du BIOS plafonnait les cœurs a
/// 4,0 GHz, ce qu'aucun indicateur de Windows ne montrait. <c>null</c> sans compteur.
/// </summary>
public static class PuissancePaquetReader
{
    private const string Requete =
        "SELECT Name, Power FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter";

    /// <summary>Watts du paquet a cet instant, ou <c>null</c>.</summary>
    public static double? LireWatts(IWmiQuery wmi)
    {
        var lu = wmi.Query(Requete);
        if (!lu.IsOk) return null;
        // Le compteur publie des milliwatts. L'instance du paquet porte « _PKG » ; `_Total` vaut 0. Un processeur allumé ne
        // tire jamais 0 W : un 0 est un compteur pas encore échantillonné (une donnée « formatée » se calcule entre deux
        // relevés), donc non lu.
        var paquet = lu.Value!.FirstOrDefault(r =>
            r.GetString("Name")?.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase) == true);
        return paquet?.GetUlong("Power") is { } mw and > 0 ? mw / 1000.0 : null;
    }
}
