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

/// <summary>Le démarrage ne lit que le manifeste signé : l'installeur (49 Mo) ne se télécharge qu'au clic, sous sa propre
/// borne. Sous la borne de démarrage, il demandait 26 Mbit/s, et chaque lancement le retéléchargeait sans qu'on l'ait demandé.</summary>
public sealed class MiseAJourLenteTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), $"drdxt-maj-lente-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ServeurLent(byte[] installeur, TimeSpan attenteInstalleur) : HttpMessageHandler
    {
        public int Installeurs { get; private set; }

        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/setup.exe", StringComparison.Ordinal))
            {
                Installeurs++;
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
    public async Task Le_demarrage_annonce_la_version_et_l_installeur_ne_part_qu_au_clic()
    {
        Directory.CreateDirectory(_racine);
        var serveur = new ServeurLent(new byte[4096], TimeSpan.Zero);
        using var client = new HttpClient(serveur);

        // Borne large : elle couvre aussi le montage (clés, crypto, dossiers), mesuré jusqu'à 7,7 s sur une CI chargée.
        var r = await VerificationMiseAJour.VerifierAsync(
            () => UpdateComposition.TryCreate("https://u.test", client, _racine, _racine, "1.0.0", UpdateDevKeys.CreateUpdateVerifier().Value!),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        r.Etat.Should().Be(EtatMiseAJour.NouvelleVersionVerifiee);
        r.Installable.Should().BeTrue();
        serveur.Installeurs.Should().Be(0, "sans clic, rien que le manifeste");

        var chemin = await r.Telecharger!(CancellationToken.None);
        chemin.IsOk.Should().BeTrue();
        File.Exists(chemin.Value).Should().BeTrue();
        serveur.Installeurs.Should().Be(1);
    }

    // Le chemin de l'App : la vérification du démarrage finie, le clic arrive plus tard, sur le client qu'elle a monté.
    // Libéré à la fin de la vérification (using), ce client faisait échouer le clic par une ObjectDisposedException.
    [Fact]
    public async Task Le_clic_Installer_plus_tard_telecharge_par_le_client_du_demarrage()
    {
        Directory.CreateDirectory(_racine);
        var serveur = new ServeurLent(new byte[4096], TimeSpan.Zero);

        var (r, client) = await VerificationMiseAJour.VerifierAuDemarrageAsync(
            c => UpdateComposition.TryCreate("https://u.test", c, _racine, _racine, "1.0.0", UpdateDevKeys.CreateUpdateVerifier().Value!),
            serveur);
        using var _ = client;
        r.Installable.Should().BeTrue();
        await Task.Yield();

        var chemin = await r.Telecharger!(CancellationToken.None);

        chemin.IsOk.Should().BeTrue(chemin.Error?.Message);
        serveur.Installeurs.Should().Be(1);
    }
}
