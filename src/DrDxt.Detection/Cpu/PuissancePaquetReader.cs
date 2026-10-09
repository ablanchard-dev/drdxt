using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// 25/09/2026 — la puissance du paquet processeur, telle que Windows la publie (compteur
/// « Energy Meter », instance RAPL du paquet). Lue par la classe CIM et non par le nom du
/// compteur : le nom est traduit (« Compteur Énergie » sur un Windows français), la classe non.
///
/// Mesure du jour sur la machine de reference, Warzone en partie : 66,5 W plats sur un
/// i7-14700F dont la Base Power est de 65 W. C'est ce chiffre qui a montre que la limite longue
/// du BIOS plafonnait les cœurs a 4,0 GHz ; aucun indicateur de Windows ne le montrait.
///
/// <c>null</c> quand le compteur n'existe pas (toutes les machines ne l'exposent pas) : jamais
/// une valeur inventee.
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
        // Le compteur publie des milliwatts. L'instance du paquet porte « _PKG » ; `_Total` vaut 0.
        var paquet = lu.Value!.FirstOrDefault(r =>
            r.GetString("Name")?.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase) == true);
        return paquet?.GetUlong("Power") is { } mw ? mw / 1000.0 : null;
    }
}
