using DrDxt.Core;
using System.Net;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using Xunit;

namespace DrDxt.Infrastructure.Tests.Updates;

/// <summary>
/// Le moteur se monte, ou il dit pourquoi il ne se monte pas.
///
/// Sans url de serveur configuree, la question n'est pas « est-ce que la mise a jour marche » mais
/// « que fait le produit tant qu'elle ne peut pas marcher ». Deux reponses seraient mauvaises et une seule est juste :
///   • monter un updater quand meme -> il echoue a chaque tour en disant « hors ligne », le client
///     lit une panne de reseau qui n'existe pas, et le jour ou le reseau tombe vraiment plus
///     personne ne regarde ce message ;
///   • ne rien dire du tout -> l'etat « pas de mise a jour » devient indistinguable d'un moteur qui
///     tourne et ne trouve rien ;
///   • REFUSER de se monter, une fois, avec un code a soi (`UPDATE_NOT_CONFIGURED`).
///
/// Et un refus doit etre silencieux sur le reseau : ces tests comptent les requetes reellement
/// emises. Un refus qui aurait quand meme tape sur le reseau ne serait pas un refus.
/// </summary>
public sealed class TheUpdaterIsMountedOrSaysWhyNotTests
{
    // Compte ce qui part vraiment sur le fil. Sans lui, « aucune tentative » ne serait qu'un nom.
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static (HttpClient Client, CountingHandler Handler) Wire()
    {
        var handler = new CountingHandler();
        return (new HttpClient(handler), handler);
    }

    private static Result<AutoUpdater> Mount(string? baseUrl, HttpClient client)
        => UpdateComposition.TryCreate(baseUrl, client, Path.GetTempPath(), @"C:\Program Files\DrDXT", "1.0.0");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_server_configured_nothing_is_mounted_and_nothing_is_attempted(string? baseUrl)
    {
        var (client, handler) = Wire();

        var mounted = Mount(baseUrl, client);

        mounted.IsOk.Should().BeFalse();
        mounted.Error!.Value.Code.Should().Be(UpdateErrorCodes.NotConfigured,
            "« pas configure » se corrige en configurant ; « hors ligne » envoie chercher une "
            + "panne de reseau qui n'existe pas");
        handler.Requests.Should().Be(0, "un refus de montage ne doit toucher aucun reseau");
    }

    [Theory]
    [InlineData("http://updates.example.invalid")]   // en clair
    [InlineData("updates.example.invalid")]          // pas une url absolue
    [InlineData("ftp://updates.example.invalid")]    // autre schema
    public void A_url_that_is_not_absolute_https_is_refused(string baseUrl)
    {
        // La signature protege le contenu du manifeste, pas le fait qu'on le recoive : en clair, un
        // intermediaire peut retenir indefiniment une mise a jour de securite en repondant « rien a
        // offrir ». Un refus de servir est invisible pour une signature.
        var (client, handler) = Wire();

        var mounted = Mount(baseUrl, client);

        mounted.IsOk.Should().BeFalse();
        mounted.Error!.Value.Code.Should().Be(UpdateErrorCodes.NotConfigured);
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public void With_an_https_server_and_a_trusted_key_the_engine_is_mounted()
    {
        // Controle POSITIF : sans lui, un montage qui refuserait toujours rendrait les tests
        // ci-dessus verts sans qu'aucune mise a jour ne soit jamais possible.
        var (client, _) = Wire();

        UpdateComposition.TryCreate("https://updates.example.invalid", client, Path.GetTempPath(),
                @"C:\Program Files\DrDXT", "1.0.0", UpdateDevKeys.CreateUpdateVerifier().Value!)
            .IsOk.Should().BeTrue();
    }

    [Fact]
    public void With_the_production_key_an_https_server_mounts_and_nothing_is_fetched_at_mount()
    {
        // Le montage verifiait avec la clef de DEV, dont la graine privee est dans le depot.
        // La clef de release existe (graine chiffree hors depot) ; le montage passe, et il ne touche
        // toujours pas le reseau : c'est la verification, plus tard, qui lit le manifeste.
        var (client, handler) = Wire();

        var mounted = Mount("https://updates.example.invalid", client);

        mounted.IsOk.Should().BeTrue(mounted.Error?.Message);
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public void The_production_key_is_a_real_key_and_not_the_dev_one()
    {
        var cle = UpdateTrust.ProductionPublicKey;

        cle.Should().NotBeNull().And.HaveCount(32, "une clef Ed25519 brute");
        cle!.AsSpan().SequenceEqual(UpdateDevKeys.UpdatePublicKey).Should().BeFalse("la clef de dev a sa graine dans le depot");
        UpdateTrust.CreateProductionVerifier().IsOk.Should().BeTrue();
    }

    [Fact]
    public void La_doc_de_mise_a_jour_donne_l_empreinte_de_la_clef_embarquee()
    {
        // Changer la clef sans le dire laisserait la doc annoncer une clef que plus aucune App ne connaît.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull();
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "MISE-A-JOUR.md"));

        doc.Should().Contain(Convert.ToHexStringLower(UpdateTrust.ProductionPublicKey!));
    }

    [Fact]
    public void The_dev_key_is_refused_as_a_production_key()
        => UpdateTrust.CreateVerifier(UpdateDevKeys.UpdatePublicKey).IsOk.Should().BeFalse(
            "sa graine privee est publique : quiconque a lu le depot signerait une mise a jour");

    [Fact]
    public void The_manifest_path_is_the_one_the_server_serves()
    {
        // Ce chemin est ecrit deux fois — ici et dans la route du serveur. Le pin ci-dessous le
        // fige ; `TheClientReadsTheRealServerTests` prouve qu'il atteint vraiment la route.
        UpdateComposition.ManifestPath(UpdateChannel.App).Should().Be("/updates/app/manifest");
    }

    [Theory]
    [InlineData("https://ablanchard-dev.github.io/drdxt", "https://ablanchard-dev.github.io/drdxt/updates/app/manifest")]
    [InlineData("https://ablanchard-dev.github.io/drdxt/", "https://ablanchard-dev.github.io/drdxt/updates/app/manifest")]
    [InlineData("https://updates.exemple.test", "https://updates.exemple.test/updates/app/manifest")]
    public void La_racine_garde_son_chemin(string racine, string attendu)
        => UpdateComposition.UrlDuManifeste(new Uri(racine), UpdateChannel.App).Should().Be(attendu);
}
