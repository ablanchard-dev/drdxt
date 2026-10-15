using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Network;

public interface INetworkAdapterReader
{
    Result<IReadOnlyList<NetworkAdapterState>> Read();

    /// <summary>Passerelle par defaut IPv4 de la carte physique donnee, ou null si elle n'en a
    /// pas. On passe par la carte plutot que par « la premiere passerelle trouvee » parce qu'un
    /// adaptateur VPN monte en a une aussi : mesurer la sienne repondrait sur le tunnel, pas sur
    /// le lien local. Ok(null) = pas de passerelle ; Fail = lecture impossible.</summary>
    Result<string?> ReadDefaultGateway(NetworkAdapterState adapter);

    /// <summary>Toutes les passerelles IPv4 par defaut publiees par la machine, distinctes et
    /// cartes virtuelles comprises. Sert a une seule question : y a-t-il ambiguite sur le premier
    /// saut ? Une seule passerelle = tout ce qui sort passe par elle. Plusieurs = rien ici ne dit
    /// laquelle porte la route vers une destination donnee, et un premier saut mesure au hasard
    /// n'est plus forcement sur le chemin auquel on le compare.</summary>
    Result<IReadOnlyList<string>> ReadAllDefaultGateways();
}

/// <summary>
/// Lit les cartes réseau physiques connectées et leurs réglages NDIS dans root\StandardCimv2.
///
/// Deux décisions à garder :
///   * les réglages se reconnaissent au mot-clé NDIS normalisé (*InterruptModeration, *RSS,
///     *EEE), jamais au DisplayName. Le DisplayName est traduit : sur cette machine il vaut
///     « Modération interruption ». Un lecteur fondé sur les noms anglais ne trouverait rien sur
///     un Windows français et dirait Unknown partout : une réponse fausse qui ressemble à une
///     fonction qui marche.
///   * les cartes virtuelles sont écartées (Virtual = TRUE) : tunnels VPN, Hyper-V/VirtualBox
///     host-only, Bluetooth PAN. Conseiller de régler une carte VirtualBox est du bruit.
///
/// Sans droits admin, lecture seule, ne lève jamais (la couture WMI convertit tout en Result).
/// </summary>
public sealed class NetworkAdapterReader : INetworkAdapterReader
{
    private const string Namespace = @"\\.\root\StandardCimv2";

    // ndisPhysicalMedium : 14 = 802.3 (Ethernet). Tout le reste (9 = 802.11 natif, …) n'est
    // pas filaire. Source : énumération NDIS_PHYSICAL_MEDIUM.
    private const int NdisPhysicalMedium8023 = 14;

    // MediaConnectState : 1 = Connected. Une carte sans câble ni association ne joue aucun rôle
    // dans la partie en cours.
    private const int MediaConnected = 1;

    private readonly IWmiQuery _wmi;

    public NetworkAdapterReader(IWmiQuery wmi) => _wmi = wmi;

    public Result<IReadOnlyList<NetworkAdapterState>> Read()
    {
        var adapters = _wmi.QueryInNamespace(Namespace,
            "SELECT Name, InterfaceDescription, Virtual, MediaConnectState, ndisPhysicalMedium FROM MSFT_NetAdapter");
        if (!adapters.IsOk)
            return Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, adapters.Error!.Value.Message);

        // La table des propriétés avancées est lue une fois et indexée ; un échec ici n'est pas
        // fatal : on sait quelles cartes existent, et chaque réglage reste simplement Unknown. Dire
        // « carte présente, réglages inconnus » est honnête ; retirer la carte la cacherait.
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
                // Les deux premiers sont des mots-cles proprietaires (Realtek), sans etoile : non
                // normalises, donc absents de bien des cartes, et leur absence rend Unknown,
                // jamais Off. `*SelectiveSuspend` est normalise, lui, donc present sur beaucoup
                // plus de machines.
                //
                // Le ou est volontaire : les trois disent la meme chose au client : le lien
                // reduit sa puissance quand il est calme, et le reveil coute des microsecondes.
                // Il n'a pas a lire trois fois la meme idee parce que sa carte l'expose sous trois
                // noms.
                //
                // `*SelectiveSuspend` est lu : il est actif sur la machine de reference, et standard.
                LinkPowerSaving: Either(
                    Flag(keywords, "EnableGreenEthernet"),
                    Flag(keywords, "PowerSavingMode"),
                    Flag(keywords, "*SelectiveSuspend"))));
        }

        return Result<IReadOnlyList<NetworkAdapterState>>.Ok(states);
    }

    /// <summary>On = l'un au moins est actif · Off = au moins un a ete lu et aucun n'est actif ·
    /// Unknown = aucun n'a pu etre lu. Un seul lu et actif suffit a dire On : l'economie d'energie
    /// est bien la. L'asymetrie est le cœur : `Unknown` ne devient jamais `Off`, sinon une carte
    /// qui n'expose aucun de ces mots-cles serait declaree sans economie d'energie.</summary>
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

    /// <summary>RegistryValue est un tableau de chaînes en CIM (à un seul élément en pratique).
    /// ToString() dessus rend « System.String[] » : on le déballe explicitement. C'est exactement
    /// le genre de lecture fausse mais plausible, et silencieuse, qui finit livrée.</summary>
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
        // Win32_NetworkAdapterConfiguration (cimv2) porte la passerelle ; MSFT_NetAdapter ne
        // l'expose pas. Les deux se recoupent sur la description de l'interface.
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

            // IPv4 uniquement : une adresse IPv6 lien-local (fe80::) est la passerelle du meme
            // saut, mais la sonde ICMP du produit vise l'IPv4 : melanger les deux comparerait
            // deux chemins differents sans le dire.
            return Result<string?>.Ok(PremiereIPv4(gws));
        }
        return Result<string?>.Ok(null);
    }

    public Result<IReadOnlyList<string>> ReadAllDefaultGateways()
    {
        // Cartes virtuelles comprises, et c'est tout l'interet de cette lecture. Le reste de ce
        // fichier ecarte les adaptateurs virtuels parce qu'on ne conseille pas de regler un tunnel
        // VPN. Mais pour savoir si le premier saut d'une carte physique est bien celui que le
        // trafic emprunte, il faut au contraire voir toutes les passerelles : un tunnel monte, ou
        // une seconde carte connectee, en publie une autre, et rien ici ne dit laquelle porte la
        // route. Deux passerelles distinctes = ambiguite, et l'appelant doit se taire.
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
