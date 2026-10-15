using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DrDxt.App.Updates;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.App.Tests;

/// <summary>La borne de démarrage tient le manifeste, pas le téléchargement de l'installeur : à 15 s, un
/// installeur de 49 Mo demandait 26 Mbit/s, et une connexion plus lente ne recevait jamais la mise à jour.</summary>
public sealed class MiseAJourLenteTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), $"drdxt-maj-lente-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ServeurLent(byte[] installeur, TimeSpan attenteInstalleur) : HttpMessageHandler
    {
        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/setup.exe", StringComparison.Ordinal))
            {
                await Task.Delay(attenteInstalleur, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installeur) };
            }
            if (url == UpdateComposition.UrlDuManifeste(new Uri("https://u.test"), UpdateChannel.App))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Signe(installeur), options: Json) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static UpdateManifest Signe(byte[] charge)
        {
            var nu = new UpdateManifest(UpdateChannel.App, "1.3.0", "https://u.test/setup.exe",
                Convert.ToHexString(SHA256.HashData(charge)).ToLowerInvariant(), Signature: string.Empty);
            using var cle = Key.Import(SignatureAlgorithm.Ed25519, UpdateDevKeys.DevPrivateSeed, KeyBlobFormat.RawPrivateKey);
            return nu with { Signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(cle, UpdateVerifier.CanonicalBytes(nu))) };
        }
    }

    [Fact]
    public async Task Un_installeur_plus_long_a_telecharger_que_la_borne_arrive_quand_meme()
    {
        Directory.CreateDirectory(_racine);
        // Borne large : elle couvre aussi le montage (clés, crypto, dossiers), mesuré jusqu'à 7,7 s sur une CI chargée.
        // L'installeur reste plus lent qu'elle, c'est ce que le test prouve.
        using var client = new HttpClient(new ServeurLent(new byte[4096], TimeSpan.FromSeconds(12)));

        var r = await VerificationMiseAJour.VerifierAsync(
            () => UpdateComposition.TryCreate("https://u.test", client, _racine, _racine, "1.0.0", UpdateDevKeys.CreateUpdateVerifier().Value!),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        r.Etat.Should().Be(EtatMiseAJour.NouvelleVersionVerifiee, "le manifeste a répondu à temps ; seul l'installeur est lent");
    }
}
