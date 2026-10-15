using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Sur un portable, la capacité à pleine charge comparée à
/// la capacité d'origine (root\WMI, BatteryStaticData.DesignedCapacity et BatteryFullChargedCapacity.FullChargedCapacity,
/// les mêmes compteurs que `powercfg /batteryreport`). Un PC fixe n'a pas ces classes : rien n'est dit.
/// </summary>
public static class UsureBatterie
{
    /// <summary>Sous 80 % de la capacité d'origine, la batterie est usée. **Non chiffré** pour un PC : c'est le seuil où
    /// Apple recommande le remplacement de ses batteries (« Batteries - Service », support.apple.com), repris faute d'un
    /// seuil publié par Microsoft.</summary>
    public const double SeuilUsee = 0.80;

    /// <summary>Un onduleur USB (périphérique HID d'alimentation) se publie aussi comme une batterie.
    /// On ne juge que sur un portable ou une console portable ; boîtier non lu : rien non plus. Le
    /// boîtier vient de <see cref="ChassisDetector"/>, la détection du reste de DrDXT (elle lisait ici sa propre liste, sous
    /// la seule forme ushort[]).</summary>
    public static IEnumerable<RepairFinding> LireEtJuger(IWmiQuery wmi)
    {
        if (new ChassisDetector(wmi).Detect() is not { IsOk: true, Value: ChassisType.Laptop or ChassisType.Handheld })
            return [];
        var origine = wmi.QueryInNamespace(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
        var pleine = wmi.QueryInNamespace(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
        if (!origine.IsOk || !pleine.IsOk || origine.Value!.Count == 0 || pleine.Value!.Count == 0) return [];
        return Juger(Somme(origine.Value!, "DesignedCapacity"), Somme(pleine.Value!, "FullChargedCapacity"));
    }

    /// <summary>Pur. Capacités en mWh (toutes batteries additionnées) ; une valeur absente ou nulle ne juge rien.</summary>
    public static IEnumerable<RepairFinding> Juger(long? origineMwh, long? pleineMwh)
    {
        if (origineMwh is not > 0 || pleineMwh is not > 0) yield break;
        var part = (double)pleineMwh.Value / origineMwh.Value;
        var pct = Math.Round(part * 100).ToString("0", CultureInfo.InvariantCulture);
        yield return part < SeuilUsee
            ? new RepairFinding("POWER", "usure de la batterie", Docteur.T("UB.Valeur", pct), Docteur.T("UB.Attendu"),
                RepairStatus.Suspicious,
                Docteur.T("UB.Usee", pct),
                RepairAction.Recommandation)
            : new RepairFinding("POWER", "usure de la batterie", Docteur.T("UB.Valeur", pct), Docteur.T("UB.Attendu"),
                RepairStatus.Observed, Docteur.T("UB.Tient", pct));
    }

    private static long? Somme(IReadOnlyList<WmiRow> lignes, string champ)
    {
        long total = 0;
        foreach (var l in lignes)
            if (long.TryParse(l.GetString(champ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0) total += v;
        return total > 0 ? total : null;
    }
}
