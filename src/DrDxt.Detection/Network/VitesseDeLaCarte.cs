using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Network;

/// <summary>
/// Une carte réseau filaire dont la vitesse ou le duplex a été forcé à autre chose que ce que le lien a négocié : le
/// lien tombe à la vitesse forcée, ou ne se monte qu'au prix de pertes. Le contrôle de flux et les économies du
/// constructeur sont dits tels que le pilote les nomme, sans verdict ni levier : leur effet en jeu n'est pas prouvé.
/// Lecture seule.
/// </summary>
/// <remarks>
/// Microsoft Learn, « Enumeration Keywords » (NDIS) : *SpeedDuplex 0 = négociation automatique, 1 à 10 = 10 Mbit/s semi à
/// 100 Gbit/s duplex intégral ; au-delà, une valeur directe d'au moins 1000 se lit en Mbit/s. *FlowControl 0 = coupé,
/// 3 = émission et réception (défaut des postes clients), 4 = négocié. La vitesse négociée et le duplex : MSFT_NetAdapter
/// (Speed en bit/s, FullDuplex). Relevé sur la machine de référence le 17/10/2026 : négociation automatique, contrôle de
/// flux 3, lien à 1 Gbit/s en duplex intégral.
/// </remarks>
public static class VitesseDeLaCarte
{
    /// <summary>Pur : la vitesse (Mbit/s) et le duplex qu'une valeur de *SpeedDuplex impose ; null = négociation
    /// automatique ou valeur non décrite par la table.</summary>
    public static (long Mbps, bool Integral)? Forcee(long valeur) => valeur switch
    {
        1 => (10, false), 2 => (10, true), 3 => (100, false), 4 => (100, true), 5 => (1000, false), 6 => (1000, true),
        7 => (10_000, true), 8 => (20_000, true), 9 => (40_000, true), 10 => (100_000, true),
        >= 1000 => (valeur, true),
        _ => null,
    };

    public sealed record Carte(string Nom, long? SpeedDuplex, long? LienMbps, bool? LienIntegral, IReadOnlyList<(string Nom, string Valeur)> Constructeur);

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Carte>? cartes)
    {
        if (cartes is null)
        {
            yield return new RepairFinding("RESEAU", "vitesse de la carte", null, null, RepairStatus.Unknown, Docteur.T("VC.NonLu"));
            yield break;
        }
        foreach (var c in cartes)
        {
            var nom = Docteur.Nom("VC.Nom", c.Nom);
            var reglages = c.Constructeur.Count > 0 ? Docteur.T("VC.Constructeur", string.Join(", ", c.Constructeur.Select(r => $"{r.Nom} = {r.Valeur}"))) : "";
            if (c.SpeedDuplex is not { } v)
            {
                yield return new RepairFinding("RESEAU", nom, null, null, RepairStatus.Unknown, Docteur.T("VC.SansMotCle") + reglages);
                continue;
            }
            var lien = c.LienMbps is { } m ? Docteur.T("VC.Lien", m, c.LienIntegral == true ? Docteur.T("VC.Integral") : Docteur.T("VC.Semi")) : Docteur.T("VC.LienNonLu");
            if (v == 0)
            {
                yield return new RepairFinding("RESEAU", nom, Docteur.T("VC.Auto"), Docteur.T("VC.Auto"), RepairStatus.ExpectedDefault,
                    Docteur.T("VC.Negocie", lien) + reglages);
                continue;
            }
            if (Forcee(v) is not { } f)
            {
                yield return new RepairFinding("RESEAU", nom, $"*SpeedDuplex = {v}", Docteur.T("VC.Auto"), RepairStatus.Observed,
                    Docteur.T("VC.Inconnue", v, lien) + reglages);
                continue;
            }
            var different = c.LienMbps is { } l && (l != f.Mbps || (c.LienIntegral is { } i && i != f.Integral));
            yield return different
                ? new RepairFinding("RESEAU", nom, Docteur.T("VC.ForceeValeur", f.Mbps, f.Integral ? Docteur.T("VC.Integral") : Docteur.T("VC.Semi")),
                    Docteur.T("VC.Auto"), RepairStatus.Suspicious, Docteur.T("VC.Incoherente", lien) + reglages, RepairAction.Recommandation)
                : new RepairFinding("RESEAU", nom, Docteur.T("VC.ForceeValeur", f.Mbps, f.Integral ? Docteur.T("VC.Integral") : Docteur.T("VC.Semi")),
                    Docteur.T("VC.Auto"), RepairStatus.Observed, Docteur.T("VC.ForceeEgale", lien) + reglages);
        }
    }

    // Les mots-clés propres aux constructeurs qui disent une économie d'énergie du lien, lus avec le nom et la valeur
    // que le pilote affiche lui-même : rien n'est interprété.
    private static readonly HashSet<string> Economies = new(StringComparer.OrdinalIgnoreCase)
        { "EnableGreenEthernet", "GigaLite", "PowerSavingMode", "AdvancedEEE", "EEELinkAdvertisement", "ReduceSpeedOnPowerDown", "WakeOnLink" };

    public static IReadOnlyList<Carte>? Lire(IWmiQuery wmi)
    {
        const string ns = @"\\.\root\StandardCimv2";
        var cartes = wmi.QueryInNamespace(ns, "SELECT Name, Speed, FullDuplex, Virtual, MediaConnectState, ndisPhysicalMedium FROM MSFT_NetAdapter");
        if (!cartes.IsOk) return null;
        var avances = wmi.QueryInNamespace(ns, "SELECT Name, RegistryKeyword, RegistryValue, DisplayName, DisplayValue FROM MSFT_NetAdapterAdvancedPropertySettingData");
        var lues = new List<Carte>();
        foreach (var l in cartes.Value!)
        {
            // Filaire (802.3), physique, câble branché : les mêmes règles que le lecteur des réglages de la carte.
            if (l.GetInt("ndisPhysicalMedium") != 14 || l.GetInt("MediaConnectState") != 1) continue;
            if (l.Properties.GetValueOrDefault("Virtual") is true) continue;
            var nom = l.GetString("Name") ?? "?";
            var siens = avances.IsOk ? avances.Value!.Where(a => a.GetString("Name") == nom).ToList() : [];
            long? speedDuplex = siens.FirstOrDefault(a => a.GetString("RegistryKeyword") == "*SpeedDuplex") is { } sd
                && sd.Properties.GetValueOrDefault("RegistryValue") is string[] { Length: > 0 } valeurs
                && long.TryParse(valeurs[0], out var n) ? n : null;
            var constructeur = siens.Where(a => Economies.Contains(a.GetString("RegistryKeyword") ?? "") || a.GetString("RegistryKeyword") == "*FlowControl")
                .Select(a => (a.GetString("DisplayName") ?? a.GetString("RegistryKeyword") ?? "?", a.GetString("DisplayValue") ?? "?")).ToList();
            long? lien = l.Properties.GetValueOrDefault("Speed") is { } s && ulong.TryParse(s.ToString(), out var bps) && bps > 0 ? (long)(bps / 1_000_000) : null;
            lues.Add(new Carte(nom, speedDuplex, lien, l.Properties.GetValueOrDefault("FullDuplex") as bool?, constructeur));
        }
        return lues;
    }
}
