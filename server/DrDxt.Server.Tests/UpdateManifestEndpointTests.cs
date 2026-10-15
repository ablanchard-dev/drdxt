using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NSec.Cryptography;

namespace DrDxt.Server.Tests;

// -----------------------------------------------------------------------
// LE MANIFESTE DE MISE A JOUR, SERVI ET SIGNE.
//
// Etat mesure le 05 et le 06/09/2026 : le moteur de mise a jour du client (AutoUpdater,
// IUpdateFeed, UpdateVerifier) est ecrit et teste, et **il ne tourne pour personne**. Cote serveur,
// `Program.cs` n'exposait que deux routes — le webhook et /health. Le signeur de manifeste existait,
// etait enregistre, et **aucune route ne l'appelait**. Un moteur sans source est un moteur a l'arret.
//
// Ce fichier est le ROUGE de depart du lot LU : il decrit la route avant qu'elle existe.
//
// ══ LES QUATRE ETATS DE LA ROUTE, ET POURQUOI CHACUN ══
//   • canal inconnu            -> 404. La liste des canaux est fermee (App).
//   • canal sans version       -> 204. Le client lit « joignable, rien a offrir » (Ok(null)) et
//                                 reste sur sa version. C'est l'etat REEL aujourd'hui : aucune
//                                 version n'est publiee. Servir un 404 ferait croire a une panne.
//   • signature impossible     -> 503, JAMAIS un manifeste nu. Un manifeste sans signature est un
//                                 manifeste que le client refusera de toute facon : autant le dire
//                                 franchement cote serveur plutot que lui envoyer du poison.
//   • canal publie et signable -> 200 + les quatre champs signes.
// -----------------------------------------------------------------------
public sealed class UpdateManifestEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _bare;

    public UpdateManifestEndpointTests(WebApplicationFactory<Program> factory) => _bare = factory;

    // Une paire Ed25519 fabriquee ici : le test signe avec la graine et verifie avec la clef
    // publique correspondante. Il ne depend d'aucun secret du depot.
    private static (string SeedBase64, PublicKey Public) NewKeyPair()
    {
        var creation = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var key = Key.Create(SignatureAlgorithm.Ed25519, creation);
        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        var imported = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return (Convert.ToBase64String(seed), imported.PublicKey);
    }

    private HttpClient Client(params (string Key, string Value)[] settings)
    {
        var factory = _bare.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", "Data Source=:memory:");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

        return factory.CreateClient();
    }

    private static (string Key, string Value)[] AppRelease(string seedBase64) =>
    [
        ("ManifestSigning:Ed25519PrivateSeedBase64", seedBase64),
        ("Updates:App:Version", "1.4.0"),
        ("Updates:App:Url", "https://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe"),
        ("Updates:App:Sha256", "9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8"),
    ];

    [Fact]
    public async Task An_unknown_channel_is_not_found()
    {
        var response = await Client().GetAsync("/updates/firmware/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "la liste des canaux est fermee : App, rien d'autre");
    }

    [Fact]
    public async Task A_channel_with_no_published_release_says_nothing_to_offer()
    {
        var (seed, _) = NewKeyPair();

        var response = await Client(("ManifestSigning:Ed25519PrivateSeedBase64", seed))
            .GetAsync("/updates/app/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "aucune version n'est publiee aujourd'hui ; le client doit lire « rien a offrir » et "
            + "rester sur sa version, pas croire a une panne");
    }

    [Fact]
    public async Task Without_a_signing_key_the_server_refuses_rather_than_serving_a_bare_manifest()
    {
        // Fail-closed. Un manifeste non signe serait refuse par le client de toute facon : le
        // servir quand meme ne fait que deplacer la panne chez lui, sans la nommer.
        var response = await Client(
                ("Updates:App:Version", "1.4.0"),
                ("Updates:App:Url", "https://example.invalid/x.exe"),
                ("Updates:App:Sha256", "00"))
            .GetAsync("/updates/app/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_published_channel_is_served_signed_over_its_canonical_bytes()
    {
        var (seed, publicKey) = NewKeyPair();

        var response = await Client(AppRelease(seed)).GetAsync("/updates/app/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("channel").GetString().Should().Be("App",
            "le nom INVARIANT de l'enum est ce que le client canonise, pas le segment d'url");
        root.GetProperty("version").GetString().Should().Be("1.4.0");
        root.GetProperty("url").GetString().Should().Be("https://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe");

        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
        var canonical = Encoding.UTF8.GetBytes(
            "{\"channel\":\"App\",\"version\":\"1.4.0\","
            + "\"url\":\"https://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe\","
            + "\"sha256\":\"9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8\"}");

        SignatureAlgorithm.Ed25519.Verify(publicKey, canonical, signature).Should().BeTrue(
            "la signature porte sur les MEMES octets que ceux que le client recompose : ordre des "
            + "champs impose, pas d'indentation, canal en nom d'enum");
    }

    [Fact]
    public async Task One_flipped_byte_of_the_served_manifest_stops_verifying()
    {
        // Controle POSITIF de l'assertion precedente : sans lui, une verification qui rend
        // toujours vrai (mauvaise clef, octets vides) laisserait le test au vert.
        var (seed, publicKey) = NewKeyPair();

        var response = await Client(AppRelease(seed)).GetAsync("/updates/app/manifest");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString()!);
        var tampered = Encoding.UTF8.GetBytes(
            "{\"channel\":\"App\",\"version\":\"1.4.1\","
            + "\"url\":\"https://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe\","
            + "\"sha256\":\"9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8\"}");

        SignatureAlgorithm.Ed25519.Verify(publicKey, tampered, signature).Should().BeFalse();
    }
}
