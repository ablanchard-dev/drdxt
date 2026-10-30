using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NSec.Cryptography;

namespace DrDxt.Server.Tests;

// -----------------------------------------------------------------------
// 19/09/2026. LE SERVEUR SIGNAIT CE QUE LA CONFIGURATION DISAIT, SANS LE REGARDER.
//
// `/updates/{channel}/manifest` lit trois valeurs dans la configuration — version, url,
// sha256 — et les SIGNE. La liste des canaux est fermee, la forme canonique est gardee
// par un test qui fait verifier le client : tout ce qui touche a la cryptographie est
// soigne.
//
// Ce qui ne l'etait pas, c'est ce que la signature couvre. Elle ne verifiait RIEN du
// contenu : une url en `http://`, un sha256 tronque, un `TODO` laisse dans le fichier de
// configuration partaient signes.
//
// ET LA SIGNATURE EST PRECISEMENT CE QUI FAIT LA CONFIANCE. Le client embarque la clef
// publique et n'accepte un manifeste que signe — c'est tout son modele de securite. Un
// serveur qui signe une valeur non verifiee transforme donc une faute de frappe en
// INSTRUCTION DE CONFIANCE. La signature ne certifie plus « ceci est un manifeste valide »
// mais « ceci vient bien de nous », et personne ne fait la difference au moment de cliquer.
//
// Trois degats concrets, du plus discret au plus grave :
//   * `http://` — le telechargement de l'installeur passe en clair. Le sha256 signe protege
//     le contenu, mais rien n'oblige quiconque a servir ce contenu-la : un reseau hostile
//     repond ce qu'il veut, et l'utilisateur voit une mise a jour qui echoue sans raison.
//     C'est aussi, mot pour mot, la ligne « forcer le HTTPS » du cahier.
//   * un sha256 qui n'est pas 64 caracteres hexadecimaux — le client refusera TOUJOURS la
//     mise a jour, silencieusement, et le produit cessera de se mettre a jour sans que
//     personne ne l'apprenne. Une panne qui ne leve rien, le pire genre.
//   * une url qui n'est pas une adresse absolue — le client ne peut rien en faire, et le
//     serveur a quand meme appose son sceau dessus.
//
// LA REGLE : ce qui ne se verifie pas ne se signe pas. Un manifeste douteux prend le chemin
// deja ecrit pour un manifeste non signable — 503, fail-closed — plutot que de deplacer la
// panne chez le client sans la nommer.
// -----------------------------------------------------------------------
public sealed class UneSignatureNeRattrapePasUneCONFIGURATIONTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _base;

    public UneSignatureNeRattrapePasUneCONFIGURATIONTests(WebApplicationFactory<Program> factory)
        => _base = factory;

    private static string NouvelleGraine()
    {
        var creation = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var key = Key.Create(SignatureAlgorithm.Ed25519, creation);
        return Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey));
    }

    private const string Sha256Valide =
        "9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    private HttpClient Client(string? url = null, string? sha256 = null, string? version = null) =>
        _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:ConnectionString", "Data Source=:memory:");
            b.UseSetting("ManifestSigning:Ed25519PrivateSeedBase64", NouvelleGraine());
            b.UseSetting("Updates:App:Version", version ?? "1.4.0");
            b.UseSetting("Updates:App:Url", url ?? "https://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe");
            b.UseSetting("Updates:App:Sha256", sha256 ?? Sha256Valide);
        }).CreateClient();

    [Fact]
    public async Task Le_temoin_une_configuration_SAINE_est_bien_servie()
    {
        // Sans lui, « tout est refuse » rendrait ce fichier entierement vert — et une route
        // qui refuse tout ne sert plus aucune mise a jour.
        var reponse = await Client().GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Une_url_en_clair_n_est_JAMAIS_signee()
    {
        var reponse = await Client(url: "http://example.invalid/drdxt/1.4.0/DrDXT-Setup.exe")
            .GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "signer une adresse en clair ferait descendre l'installeur sans protection de "
            + "transport, avec notre sceau dessus");
    }

    [Fact]
    public async Task Une_url_qui_n_est_pas_une_adresse_absolue_n_est_pas_signee()
    {
        var reponse = await Client(url: "drdxt/1.4.0/DrDXT-Setup.exe")
            .GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("TODO")]
    [InlineData("9f2c1b0d")]                                   // tronque
    [InlineData("zzzz1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8")]  // pas hexa
    public async Task Un_sha256_qui_n_en_est_pas_un_n_est_pas_signe(string sha)
    {
        // C'est la panne la plus silencieuse des trois : le client refuse, toujours, et le
        // produit cesse de se mettre a jour sans que personne ne l'apprenne.
        var reponse = await Client(sha256: sha).GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Un_canal_sans_version_publiee_repond_toujours_RIEN_A_OFFRIR()
    {
        // Contrepoids : ajouter des verifications ne doit pas transformer « rien a offrir »
        // en panne. Un canal vide est l'etat NORMAL aujourd'hui ; un 503 ferait conclure le
        // client a une mise a jour cassee, et c'est exactement ce que le code evitait deja.
        var client = _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:ConnectionString", "Data Source=:memory:");
            b.UseSetting("ManifestSigning:Ed25519PrivateSeedBase64", NouvelleGraine());
        }).CreateClient();

        var reponse = await client.GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // La version est le quatrième champ signé. Le client la lit chiffre par chiffre (UpdateVerifier.ParseVersion), un morceau
    // illisible valant 0 : « v1.4.0 » se lit 0.4.0, plus ancienne que toute version installée, et la mise à jour est refusée
    // comme un retour en arrière, à chaque démarrage, sans que personne l'apprenne.
    [Theory]
    [InlineData("v1.4.0")]
    [InlineData("TODO")]
    [InlineData("1.4.0 beta")]
    [InlineData("1..4")]
    public async Task Une_version_que_le_client_ne_lit_pas_n_est_JAMAIS_signee(string version)
    {
        var reponse = await Client(version: version).GetAsync("/updates/app/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            $"« {version} » se lirait comme une version plus ancienne, et le client refuserait toujours la mise à jour");
    }

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("1.4.0-rc.2")]
    [InlineData("1.4.0+abc1234")]
    public async Task Une_version_SemVer_reste_servie(string version)
        => (await Client(version: version).GetAsync("/updates/app/manifest")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Un_canal_inconnu_reste_introuvable()
    {
        // Second contrepoids : la nouvelle porte ne doit pas remplacer les anciennes reponses.
        var reponse = await Client().GetAsync("/updates/inconnu/manifest");

        reponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
