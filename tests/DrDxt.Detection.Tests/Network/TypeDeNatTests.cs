using System.Net;
using DrDxt.Core;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// Le type de NAT d'après l'adresse WAN de la box, et les gardes de l'échange UPnP.
public sealed class TypeDeNatTests
{
    private static readonly IPAddress Box = IPAddress.Parse("192.168.1.254");

    [Theory]
    [InlineData("100.64.0.1", TypeDeNat.Nat.Cgnat)]
    [InlineData("100.127.255.254", TypeDeNat.Nat.Cgnat)]
    [InlineData("100.128.0.1", TypeDeNat.Nat.Simple)]
    [InlineData("192.0.0.2", TypeDeNat.Nat.Tunnel)]
    [InlineData("192.0.0.8", TypeDeNat.Nat.Simple)]
    [InlineData("10.0.0.2", TypeDeNat.Nat.Double)]
    [InlineData("192.168.0.10", TypeDeNat.Nat.Double)]
    [InlineData("203.0.113.7", TypeDeNat.Nat.Simple)]
    [InlineData("0.0.0.0", TypeDeNat.Nat.NonLu)]
    public void L_adresse_wan_se_classe_par_plage(string wan, TypeDeNat.Nat attendu)
        => TypeDeNat.Classer(IPAddress.Parse(wan)).Should().Be(attendu);

    // Une URL donnée par un appareil du réseau n'est suivie que vers ce même appareil, en http et en adresse privée.
    [Theory]
    [InlineData("http://192.168.1.254:46073/rootDesc.xml", true)]
    [InlineData("https://192.168.1.254/rootDesc.xml", false)]
    [InlineData("http://192.168.1.10/rootDesc.xml", false)]
    [InlineData("http://example.com/rootDesc.xml", false)]
    [InlineData("http://203.0.113.7/rootDesc.xml", false)]
    public void Seule_une_url_http_privee_sur_l_adresse_du_repondeur_est_suivie(string url, bool suivie)
        => (TypeDeNat.UrlSure(url, Box) is not null).Should().Be(suivie);

    [Fact]
    public void Un_xml_avec_dtd_est_refuse()
        => TypeDeNat.Xml("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY e \"x\">]><r>&e;</r>").Should().BeNull();

    [Fact]
    public void La_description_donne_le_controle_et_la_reponse_l_adresse()
    {
        var description = TypeDeNat.Xml("<root><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>"
            + "<controlURL>/ctl/IPConn</controlURL></service></serviceList></device></root>")!;
        var c = TypeDeNat.Controle(description, Box, new Uri("http://192.168.1.254:46073/rootDesc.xml"))!.Value;
        c.Url.ToString().Should().Be("http://192.168.1.254:46073/ctl/IPConn");
        TypeDeNat.AdresseWan("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"x\">"
            + "<NewExternalIPAddress>100.70.1.2</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>")
            .Should().Be(IPAddress.Parse("100.70.1.2"));
    }

    // L'adresse publique n'est jamais affichée.
    [Fact]
    public void Le_constat_ne_porte_aucune_adresse_et_le_double_nat_a_son_etape()
    {
        var simple = TypeDeNat.Juger(TypeDeNat.Nat.Simple);
        simple.Statut.Should().Be(RepairStatus.ExpectedDefault);
        simple.ValeurCourante.Should().NotMatchRegex(@"\d+\.\d+\.\d+\.\d+");
        TypeDeNat.Juger(TypeDeNat.Nat.Double).Action.Should().Be(RepairAction.Recommandation);
        TypeDeNat.Juger(TypeDeNat.Nat.AucunePasserelle).Statut.Should().Be(RepairStatus.Unknown);
    }

    // 192.0.0.0/29 n'est pas « le DS-Lite » : RFC 7335 l'a ouverte à tout mécanisme de continuité IPv4.
    [Fact]
    public void La_plage_de_continuite_ipv4_se_dit_tunnel_de_l_operateur_avec_ses_deux_rfc()
    {
        var c = TypeDeNat.Juger(TypeDeNat.Nat.Tunnel);
        c.ValeurCourante.Should().Be(Docteur.T("NAT.ValeurTunnel"));
        c.Explication.Should().Contain("RFC 6333").And.Contain("RFC 7335");
    }

    // Lecture seule : la seule action UPnP envoyée est GetExternalIPAddress (aucune ouverture ni fermeture de port, aucun
    // réglage de la box), la recherche ne passe aucun routeur, et chaque échange est borné dans le temps.
    [Fact]
    public void L_echange_upnp_ne_fait_que_lire_et_reste_sur_le_reseau_de_la_maison()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var code = string.Join("\n", File.ReadAllLines(Path.Combine(d!.FullName, "src", "DrDxt.Detection", "Network", "TypeDeNat.cs"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        code.Should().Contain("#GetExternalIPAddress", "témoin : l'action de lecture a été vue");

        System.Text.RegularExpressions.Regex.Matches(code, @"#(\w+)").Select(m => m.Groups[1].Value).Distinct()
            .Should().Equal("GetExternalIPAddress");
        System.Text.RegularExpressions.Regex.Matches(code, @"<u:(\w+)").Select(m => m.Groups[1].Value).Distinct()
            .Should().Equal("GetExternalIPAddress");
        code.Should().NotContainAny("PortMapping", "SetConnectionType", "RequestConnection", "ForceTermination", "RequestTermination",
            "SetAutoDisconnectTime", "SetIdleDisconnectTime", "SetWarnDisconnectDelay");
        System.Text.RegularExpressions.Regex.Matches(code, @"HttpMethod\.(\w+)").Select(m => m.Groups[1].Value).Should().Equal("Post");
        code.Should().Contain("MulticastTimeToLive, 1").And.Contain("Timeout = Delai").And.Contain("CancelAfter(Delai)")
            .And.Contain("UseProxy = false").And.Contain("AllowAutoRedirect = false");
    }
}
