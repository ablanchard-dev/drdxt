using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using DrDxt.TestSupport;

namespace DrDxt.Infrastructure.Tests.Updates;

/// <summary>
/// Le vrai client contre le vrai serveur — la seule epreuve qui interroge la COUTURE.
///
/// Les deux moities ont chacune leurs tests, et chacune peut etre entierement verte pendant que la
/// couture est morte : le serveur repond exactement ce qu'il a decide de repondre, le client lit
/// exactement ce qu'on lui a donne a lire dans ses faux, et personne ne les met face a face. C'est
/// le motif que ce depot a deja paye trois fois — un composant ecrit, teste, et appele par personne.
///
/// Ici, `HttpUpdateFeed` — le vrai, celui du produit — parle a l'application serveur reelle montee
/// en memoire. Aucun faux entre les deux.
/// </summary>
public sealed class TheClientReadsTheRealServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Version = "1.4.0";
    private const string Url = "https://updates.example.invalid/drdxt/1.4.0/DrDXT-Setup.exe";
    private const string Sha256 = "9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    private readonly WebApplicationFactory<Program> _bare;

    public TheClientReadsTheRealServerTests(WebApplicationFactory<Program> factory) => _bare = factory;

    private HttpUpdateFeed FeedAgainst(params (string Key, string Value)[] settings)
    {
        var factory = _bare.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", "Data Source=:memory:");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

        // Le client du produit, avec la carte d'url du MONTAGE reel — pas un chemin recopie ici.
        // C'est ce qui fait de ce test la preuve que `UpdateComposition.ManifestPath` atteint bien
        // la route du serveur : recopie, il aurait prouve que deux copies se ressemblent.
        return new HttpUpdateFeed(factory.CreateClient(), UpdateComposition.ManifestPath);
    }

    private static (string Key, string Value)[] PublishedApp() =>
    [
        ("ManifestSigning:Ed25519PrivateSeedBase64",
            Convert.ToBase64String(UpdateDevTestKeys.DevPrivateSeed)),
        ("Updates:App:Version", Version),
        ("Updates:App:Url", Url),
        ("Updates:App:Sha256", Sha256),
    ];

    [Fact]
    public async Task A_published_manifest_is_read_and_verified_end_to_end()
    {
        var feed = FeedAgainst(PublishedApp());

        var fetched = await feed.GetManifestAsync(UpdateChannel.App, CancellationToken.None);

        fetched.IsOk.Should().BeTrue("le serveur publie une version, le client doit la lire");
        fetched.Value.Should().NotBeNull();
        fetched.Value!.Channel.Should().Be(UpdateChannel.App,
            "le canal voyage en NOM d'enum dans le json ; s'il est lu comme un nombre, la lecture "
            + "echoue et l'echec ressemble a une panne reseau");
        fetched.Value.Version.Should().Be(Version);
        fetched.Value.Url.Should().Be(Url);

        UpdateDevKeys.CreateUpdateVerifier().Value!
            .VerifyManifest(fetched.Value)
            .IsOk.Should().BeTrue("le manifeste arrive du reseau doit passer la porte de confiance");
    }

    [Fact]
    public async Task Nothing_published_is_read_as_nothing_to_offer_not_as_a_breakdown()
    {
        // L'etat reel aujourd'hui : aucune version n'est publiee. Le serveur repond 204. Si le
        // client le lit comme un echec, le produit dira « mise a jour indisponible » a chaque
        // demarrage alors que tout va bien — et le jour ou le reseau tombera vraiment, plus
        // personne ne regardera ce message.
        var feed = FeedAgainst(("ManifestSigning:Ed25519PrivateSeedBase64",
            Convert.ToBase64String(UpdateDevTestKeys.DevPrivateSeed)));

        var fetched = await feed.GetManifestAsync(UpdateChannel.App, CancellationToken.None);

        fetched.IsOk.Should().BeTrue("« joignable, rien a offrir » n'est pas une panne");
        fetched.Value.Should().BeNull();
    }

    [Fact]
    public async Task An_unsigned_server_is_read_as_offline_rather_than_trusted()
    {
        // Sans graine, le serveur rend 503 plutot qu'un manifeste nu. Le client doit le traiter
        // comme une indisponibilite : rester sur sa version, ne rien installer.
        var feed = FeedAgainst(
            ("Updates:App:Version", Version),
            ("Updates:App:Url", Url),
            ("Updates:App:Sha256", Sha256));

        var fetched = await feed.GetManifestAsync(UpdateChannel.App, CancellationToken.None);

        fetched.IsOk.Should().BeFalse();
        fetched.Error!.Value.Code.Should().Be(UpdateErrorCodes.Offline);
    }
}
