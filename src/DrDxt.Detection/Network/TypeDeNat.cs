using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using DrDxt.Core;

namespace DrDxt.Detection.Network;

/// <summary>
/// Pourquoi un NAT « strict » : un seul NAT (la box), un double NAT (box derrière box), un NAT de l'opérateur (CGNAT) ou
/// un NAT de l'opérateur au bout d'un tunnel (DS-Lite ou semblable). DrDXT demande seulement son adresse WAN à la box, par UPnP, et la classe par plage. Lecture seule : aucun port
/// n'est ouvert, rien ne sort du réseau de la maison. L'adresse publique n'est jamais affichée.
/// </summary>
/// <remarks>
/// Sources : UPnP Forum, InternetGatewayDevice:1 et WANIPConnection:1 (action GetExternalIPAddress, argument de sortie
/// NewExternalIPAddress) ; RFC 6598 (100.64.0.0/10, espace partagé des opérateurs) ; RFC 6333 puis RFC 7335 (192.0.0.0/29, plage de
/// continuité IPv4 : réservée d'abord au DS-Lite, ouverte ensuite aux mécanismes semblables) ;
/// RFC 1918 (adresses privées). Relevé sur la machine de référence le 17/10/2026 : la box répond à la recherche SSDP de
/// InternetGatewayDevice:1 depuis 192.168.1.254, description en http sur cette même adresse.
/// Gardes : seules les réponses dont la description et le contrôle sont en http, en IPv4 privée, sur l'adresse même du
/// répondeur, sont suivies ; XML sans DTD ; chaque échange plafonné en temps et en taille.
/// </remarks>
public static class TypeDeNat
{
    public enum Nat { NonLu, AucunePasserelle, Simple, Double, Cgnat, Tunnel }

    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(3);
    private const int OctetsMax = 64 * 1024;

    /// <summary>Pur : la classe d'une adresse WAN donnée par la box.</summary>
    public static Nat Classer(IPAddress? wan)
    {
        if (wan is null || wan.AddressFamily != AddressFamily.InterNetwork) return Nat.NonLu;
        var o = wan.GetAddressBytes();
        if (o[0] == 0) return Nat.NonLu;
        if (o[0] == 100 && (o[1] & 0xC0) == 64) return Nat.Cgnat;
        if (o[0] == 192 && o[1] == 0 && o[2] == 0 && o[3] < 8) return Nat.Tunnel;
        if (EstPrivee(wan)) return Nat.Double;
        return Nat.Simple;
    }

    /// <summary>RFC 1918.</summary>
    public static bool EstPrivee(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var o = a.GetAddressBytes();
        return o[0] == 10 || (o[0] == 172 && o[1] is >= 16 and <= 31) || (o[0] == 192 && o[1] == 168);
    }

    /// <summary>Pur : une URL de la box n'est suivie que si elle est en http, en IPv4 privée, sur l'adresse du répondeur.</summary>
    public static Uri? UrlSure(string? texte, IPAddress repondeur, Uri? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        if (!(baseUrl is null ? Uri.TryCreate(texte.Trim(), UriKind.Absolute, out var u) : Uri.TryCreate(baseUrl, texte.Trim(), out u))) return null;
        if (u.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(u.Host, out var hote)) return null;
        return hote.Equals(repondeur) && EstPrivee(hote) ? u : null;
    }

    /// <summary>Pur : XML sans DTD ni ressource externe.</summary>
    public static XmlDocument? Xml(string texte)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using var r = XmlReader.Create(new StringReader(texte), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(r);
            return doc;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>Pur : l'URL de contrôle de WANIPConnection ou WANPPPConnection dans la description, et son type.</summary>
    public static (Uri Url, string Type)? Controle(XmlDocument description, IPAddress repondeur, Uri baseUrl)
    {
        foreach (XmlNode service in description.GetElementsByTagName("service"))
        {
            var type = service["serviceType"]?.InnerText.Trim();
            if (type is null || !(type.Contains(":WANIPConnection:", StringComparison.Ordinal) || type.Contains(":WANPPPConnection:", StringComparison.Ordinal)))
                continue;
            if (UrlSure(service["controlURL"]?.InnerText, repondeur, baseUrl) is { } url) return (url, type);
        }
        return null;
    }

    /// <summary>Pur : NewExternalIPAddress dans la réponse SOAP.</summary>
    public static IPAddress? AdresseWan(string reponse)
        => Xml(reponse)?.GetElementsByTagName("NewExternalIPAddress") is { Count: > 0 } l && IPAddress.TryParse(l[0]!.InnerText.Trim(), out var a) ? a : null;

    public static async Task<Nat> LireAsync(CancellationToken ct = default)
    {
        try
        {
            var trouve = await ChercherAsync(ct).ConfigureAwait(false);
            if (trouve is not { } p) return Nat.AucunePasserelle;
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Delai, MaxResponseContentBufferSize = OctetsMax };
            var description = Xml(await http.GetStringAsync(p.Description, ct).ConfigureAwait(false));
            if (description is null || Controle(description, p.Repondeur, p.Description) is not { } c) return Nat.NonLu;
            // Attributs entre apostrophes : les espaces de noms de SOAP 1.1 sont des identifiants, pas des adresses contactées.
            var corps = "<?xml version='1.0'?><s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/' s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'>"
                + $"<s:Body><u:GetExternalIPAddress xmlns:u='{SecurityElementEscape(c.Type)}'/></s:Body></s:Envelope>";
            using var demande = new HttpRequestMessage(HttpMethod.Post, c.Url) { Content = new StringContent(corps, Encoding.UTF8, "text/xml") };
            demande.Headers.TryAddWithoutValidation("SOAPAction", $"\"{c.Type}#GetExternalIPAddress\"");
            using var reponse = await http.SendAsync(demande, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return Nat.NonLu;
            return Classer(AdresseWan(await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or SocketException or IOException or InvalidOperationException)
        {
            return Nat.NonLu;
        }
    }

    private static string SecurityElementEscape(string s) => System.Security.SecurityElement.Escape(s);

    // M-SEARCH en multicast, réponses lues pendant le délai ; la première réponse dont LOCATION est sûre gagne.
    private static async Task<(IPAddress Repondeur, Uri Description)?> ChercherAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        // Un saut : la recherche ne passe aucun routeur, elle reste sur le réseau de la maison.
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var recherche = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n"
            + "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(recherche, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct).ConfigureAwait(false);
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fin.CancelAfter(Delai);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(fin.Token).ConfigureAwait(false);
                if (r.Buffer.Length > 4096) continue;
                var location = Encoding.ASCII.GetString(r.Buffer).Split("\r\n")
                    .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..];
                if (UrlSure(location, r.RemoteEndPoint.Address) is { } url) return (r.RemoteEndPoint.Address, url);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public static RepairFinding Juger(Nat nat)
    {
        const string nom = "type de NAT";
        return nat switch
        {
            Nat.Simple => new RepairFinding("RESEAU", nom, Docteur.T("NAT.ValeurSimple"), null, RepairStatus.ExpectedDefault, Docteur.T("NAT.Simple")),
            Nat.Double => new RepairFinding("RESEAU", nom, Docteur.T("NAT.ValeurDouble"), Docteur.T("NAT.ValeurSimple"), RepairStatus.Suspicious,
                Docteur.T("NAT.Double"), RepairAction.Recommandation),
            Nat.Cgnat => new RepairFinding("RESEAU", nom, Docteur.T("NAT.ValeurCgnat"), Docteur.T("NAT.ValeurSimple"), RepairStatus.Observed, Docteur.T("NAT.Cgnat")),
            Nat.Tunnel => new RepairFinding("RESEAU", nom, Docteur.T("NAT.ValeurTunnel"), Docteur.T("NAT.ValeurSimple"), RepairStatus.Observed,
                Docteur.T("NAT.DsLite")),
            Nat.AucunePasserelle => new RepairFinding("RESEAU", nom, null, null, RepairStatus.Unknown, Docteur.T("NAT.AucunePasserelle")),
            _ => new RepairFinding("RESEAU", nom, null, null, RepairStatus.Unknown, Docteur.T("NAT.NonLu")),
        };
    }
}
