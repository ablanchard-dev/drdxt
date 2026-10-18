using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DrDxt.App.Platform;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>Le proxy par défaut du processus est remplacé le temps d'un test : aucun autre client ne doit le croiser.</summary>
[CollectionDefinition(Nom, DisableParallelization = true)]
public sealed class CollectionProxyParDefaut
{
    public const string Nom = "Proxy par défaut du processus";
}

// Un proxy découvert par WPAD répondait 407 et recevait l'échange NTLM du compte, à chaque lancement et sans clic.
[Collection(CollectionProxyParDefaut.Nom)]
public sealed class ProxyDuCompteTests
{
    [Theory]
    [InlineData(true, null, null, null, false)]                       // Windows par défaut : détection automatique
    [InlineData(true, null, "proxy.entreprise:8080", null, false)]    // le proxy WPAD passe avant le manuel
    [InlineData(false, "http://wpad/proxy.pac", "proxy:8080", null, false)]
    [InlineData(false, "http://wpad/proxy.pac", null, null, false)]
    [InlineData(false, null, null, null, false)]                      // pas de proxy
    [InlineData(false, null, "proxy.entreprise:8080", null, true)]    // proxy manuel seul
    [InlineData(true, null, null, "http://proxy:3128", true)]         // la variable remplace le proxy du système
    public void Les_identifiants_ne_vont_qu_a_un_proxy_configure(bool detection, string? script, string? manuel, string? env, bool attendu)
        => ProxyDuCompte.IdentifiantsAutorises(new(detection, script, manuel, env)).Should().Be(attendu);

    [Fact]
    public async Task Un_proxy_decouvert_qui_repond_407_ne_recoit_rien_du_compte()
    {
        var vus = await Demandes(new ProxyDuCompte.Configuration(DetectionAuto: true, null, null, null));
        vus.Should().NotBeEmpty("témoin : la demande est passée par le proxy");
        vus.Should().NotContain(d => d.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Un_proxy_manuel_seul_recoit_les_identifiants_apres_407()
    {
        var vus = await Demandes(new ProxyDuCompte.Configuration(DetectionAuto: false, null, "127.0.0.1:1", null));
        vus.Should().Contain(d => d.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase),
            "témoin : un proxy d'entreprise configuré à la main obtient toujours son authentification");
    }

    [Fact]
    public void Une_configuration_illisible_ne_donne_rien()
        => ProxyDuCompte.Transport(lire: () => throw new InvalidOperationException()).DefaultProxyCredentials.Should().BeNull();

    [Fact]
    public void Le_test_de_ligne_garde_son_refus_des_redirections()
        => ProxyDuCompte.Transport(redirections: false, lire: () => new(true, null, null, null)).AllowAutoRedirect.Should().BeFalse();

    // Un faux proxy local qui exige NTLM ; le transport de l'App passe par lui comme par celui du système.
    private static async Task<List<string>> Demandes(ProxyDuCompte.Configuration configuration)
    {
        var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        var vus = new List<string>();
        var serveur = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await ecoute.AcceptTcpClientAsync();
                    var flux = c.GetStream();
                    var tampon = new byte[8192];
                    int lus;
                    while ((lus = await flux.ReadAsync(tampon)) > 0)
                    {
                        lock (vus) vus.Add(Encoding.ASCII.GetString(tampon, 0, lus));
                        await flux.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: NTLM\r\nProxy-Authenticate: Negotiate\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                    }
                }
            }
            catch (Exception) { } // écoute arrêtée
        });
        var avant = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)ecoute.LocalEndpoint).Port}");
        try
        {
            using var client = new HttpClient(ProxyDuCompte.Transport(lire: () => configuration));
            try { await client.GetAsync("https://exemple.invalid/manifeste.json").WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception) { } // le faux proxy ne laisse jamais passer
        }
        finally
        {
            HttpClient.DefaultProxy = avant;
            ecoute.Stop();
            await serveur;
        }
        lock (vus) return [.. vus];
    }
}
