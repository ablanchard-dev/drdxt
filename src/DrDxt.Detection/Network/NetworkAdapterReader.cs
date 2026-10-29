using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Network;

public interface INetworkAdapterReader
{
    Result<IReadOnlyList<NetworkAdapterState>> Read();

    /// <summary>Passerelle par defaut IPv4 de la carte physique donnee, pas « la premiere trouvee » (un VPN monte en a une,
    /// qui repondrait sur le tunnel). Ok(null) = pas de passerelle ; Fail = lecture impossible.</summary>
    Result<string?> ReadDefaultGateway(NetworkAdapterState adapter);

    /// <summary>Toutes les passerelles IPv4 par defaut de la machine, distinctes, cartes virtuelles comprises : y a-t-il
    /// ambiguite sur le premier saut ? Une seule = tout sort par elle ; plusieurs = le premier saut mesure n'est peut-etre
    /// pas sur le chemin compare.</summary>
    Result<IReadOnlyList<string>> ReadAllDefaultGateways();
}

/// <summary>
/// Lit les cartes réseau physiques connectées et leurs réglages NDIS dans root\StandardCimv2.
///
///   * les réglages se reconnaissent au mot-clé NDIS normalisé (*InterruptModeration, *RSS, *EEE), jamais au
///     DisplayName, traduit (« Modération interruption » sur cette machine) : en anglais, Unknown partout ;
///   * les cartes virtuelles sont écartées (Virtual = TRUE) : tunnels VPN, Hyper-V/VirtualBox host-only, Bluetooth PAN.
/// Sans droits admin, lecture seule, ne lève jamais (la couture WMI convertit tout en Result).
/// </summary>
public sealed class NetworkAdapterReader : INetworkAdapterReader
{
    private const string Namespace = @"\\.\root\StandardCimv2";

    // ndisPhysicalMedium : 14 = 802.3 (Ethernet). Tout le reste (9 = 802.11 natif, …) n'est
    // pas filaire. Source : énumération NDIS_PHYSICAL_MEDIUM.
    private const int NdisPhysicalMedium8023 = 14;

    // MediaConnectState : 1 = Connected ; une carte sans câble ni association ne joue aucun rôle dans la partie.
    private const int MediaConnected = 1;

    private readonly IWmiQuery _wmi;

    public NetworkAdapterReader(IWmiQuery wmi) => _wmi = wmi;

    public Result<IReadOnlyList<NetworkAdapterState>> Read()
    {
        var adapters = _wmi.QueryInNamespace(Namespace,
            "SELECT Name, InterfaceDescription, Virtual, MediaConnectState, ndisPhysicalMedium FROM MSFT_NetAdapter");
        if (!adapters.IsOk)
            return Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, adapters.Error!.Value.Message);

        // Propriétés avancées lues une fois et indexées ; un échec n'est pas fatal : la carte reste, réglages Unknown.
        var settings = _wmi.QueryInNamespace(Namespace,
            "SELECT Name, RegistryKeyword, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData");
        var byAdapter = IndexSettings(settings);

        var states = new List<NetworkAdapterState>();
        foreach (var row in adapters.Value!)
        {
            if (IsTrue(row, "Virtual")) continue;
            if (row.GetInt("MediaConnectState") != MediaConnected) continue;

            string name = row.GetString("Name") ?? "Unknown";
            var keywords = byAdapter.TryGetValue(name, out var k) ? k : EmptyKeywords;

            states.Add(new NetworkAdapterState(
                Name: name,
                Description: row.GetString("InterfaceDescription") ?? "Unknown",
                IsWired: row.GetInt("ndisPhysicalMedium") == NdisPhysicalMedium8023,
                InterruptModeration: Flag(keywords, "*InterruptModeration"),
                Rss: Flag(keywords, "*RSS"),
                EnergyEfficientEthernet: Flag(keywords, "*EEE"),
                // Les deux premiers sont proprietaires (Realtek, sans etoile), absents de bien des cartes : absence =
                // Unknown, jamais Off. `*SelectiveSuspend` est normalise (actif sur la machine de reference). Un ou
                // volontaire : les trois disent que le lien reduit sa puissance au calme, et que le reveil coute des
                // microsecondes.
                LinkPowerSaving: Either(
                    Flag(keywords, "EnableGreenEthernet"),
                    Flag(keywords, "PowerSavingMode"),
                    Flag(keywords, "*SelectiveSuspend"))));
        }

        return Result<IReadOnlyList<NetworkAdapterState>>.Ok(states);
    }

    /// <summary>On = l'un au moins est actif · Off = au moins un a ete lu et aucun n'est actif ·
    /// Unknown = aucun n'a pu etre lu. `Unknown` ne devient jamais `Off` : une carte sans aucun de ces mots-cles serait
    /// declaree sans economie d'energie.</summary>
    private static TriState Either(params TriState[] etats)
        => etats.Any(e => e == TriState.On) ? TriState.On
         : etats.Any(e => e == TriState.Off) ? TriState.Off
         : TriState.Unknown;

    private static readonly Dictionary<string, string> EmptyKeywords = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, Dictionary<string, string>> IndexSettings(Result<IReadOnlyList<WmiRow>> settings)
    {
        var byAdapter = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!settings.IsOk) return byAdapter;

        foreach (var row in settings.Value!)
        {
            string? adapter = row.GetString("Name");
            string? keyword = row.GetString("RegistryKeyword");
            string? value = FirstValue(row);
            if (adapter is null || keyword is null || value is null) continue;

            if (!byAdapter.TryGetValue(adapter, out var map))
                byAdapter[adapter] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[keyword] = value;
        }
        return byAdapter;
    }

    /// <summary>RegistryValue est un tableau de chaînes en CIM (un élément en pratique) : ToString() rendrait
    /// « System.String[] », on le déballe.</summary>
    private static string? FirstValue(WmiRow row)
    {
        if (!row.Properties.TryGetValue("RegistryValue", out var raw) || raw is null) return null;
        return raw switch
        {
            string s => s.Trim(),
            string[] a => a.Length > 0 ? a[0]?.Trim() : null,
            System.Collections.IEnumerable e => e.Cast<object?>().FirstOrDefault()?.ToString()?.Trim(),
            _ => raw.ToString()?.Trim()
        };
    }

    /// <summary>Mot-clé NDIS quasi booléen → TriState. Mot-clé absent = Unknown (cette carte
    /// n'expose pas le réglage), pas Off.</summary>
    private static TriState Flag(Dictionary<string, string> keywords, string keyword)
    {
        if (!keywords.TryGetValue(keyword, out var v)) return TriState.Unknown;
        return v switch
        {
            "0" => TriState.Off,
            "1" => TriState.On,
            // *InterruptModeration et *RSS valent 0/1. Tout le reste (un fabricant avec une énumération
            // plus large) nous échappe → on dit Unknown au lieu de deviner.
            _ => TriState.Unknown
        };
    }

    private static bool IsTrue(WmiRow row, string key)
        => row.Properties.TryGetValue(key, out var v) && v is bool b && b;

    public Result<string?> ReadDefaultGateway(NetworkAdapterState adapter)
    {
        // La passerelle est dans Win32_NetworkAdapterConfiguration (cimv2), pas dans MSFT_NetAdapter ; recoupés par la
        // description de l'interface.
        var rows = _wmi.Query(
            "SELECT Description, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
        if (!rows.IsOk)
            return Result<string?>.Fail(ErrorCodes.WmiFail, rows.Error!.Value.Message);

        foreach (var row in rows.Value!)
        {
            if (!string.Equals(row.GetString("Description"), adapter.Description, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!row.Properties.TryGetValue("DefaultIPGateway", out var raw) || raw is not string[] gws)
                return Result<string?>.Ok(null);

            return Result<string?>.Ok(PremiereIPv4(gws));
        }
        return Result<string?>.Ok(null);
    }

    public Result<IReadOnlyList<string>> ReadAllDefaultGateways()
    {
        // Cartes virtuelles comprises, au contraire du reste du fichier : un tunnel monte ou une seconde carte publie une
        // autre passerelle, et rien ne dit laquelle porte la route. Deux distinctes = ambiguite, l'appelant se tait.
        var rows = _wmi.Query(
            "SELECT DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
        if (!rows.IsOk)
            return Result<IReadOnlyList<string>>.Fail(ErrorCodes.WmiFail, rows.Error!.Value.Message);

        var passerelles = new List<string>();
        foreach (var row in rows.Value!)
        {
            if (!row.Properties.TryGetValue("DefaultIPGateway", out var raw) || raw is not string[] gws)
                continue;
            var ipv4 = PremiereIPv4(gws);
            if (ipv4 is not null && !passerelles.Contains(ipv4, StringComparer.OrdinalIgnoreCase))
                passerelles.Add(ipv4);
        }
        return Result<IReadOnlyList<string>>.Ok(passerelles);
    }

    /// <summary>IPv4 uniquement : une adresse IPv6 lien-local (fe80::) est la passerelle du meme
    /// saut, mais la sonde ICMP du produit vise l'IPv4 : melanger les deux comparerait deux
    /// chemins differents sans le dire.</summary>
    private static string? PremiereIPv4(string[] passerelles)
    {
        foreach (var gw in passerelles)
            if (!string.IsNullOrWhiteSpace(gw) && gw.Contains('.') && !gw.Contains(':'))
                return gw.Trim();
        return null;
    }
}
