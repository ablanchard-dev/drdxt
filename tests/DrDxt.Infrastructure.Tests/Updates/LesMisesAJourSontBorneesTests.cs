using System.Net;
using DrDxt.Infrastructure.Updates;
using DrDxt.TestSupport;
using FluentAssertions;
using Xunit;

namespace DrDxt.Infrastructure.Tests.Updates;

// Ce qui arrive du serveur des mises à jour est lu avant d'être vérifié. Il suffit de tenir la réponse HTTPS (le serveur, ou
// un proxy d'entreprise qui inspecte TLS) pour l'envoyer : chaque lecture a donc son plafond, et rien ne lève.
public sealed class LesMisesAJourSontBorneesTests
{
    private sealed class SansFin : Stream
    {
        public long Lus;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)' ', offset, count); Lus += count; return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Serveur(Func<HttpContent> corps) : HttpMessageHandler
    {
        public int Demandes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Demandes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = corps() });
        }
    }

    private static HttpUpdateFeed Flux(Serveur s) => new(new HttpClient(s), _ => "https://updates.example.invalid/manifest.json");

    [Fact]
    public async Task Un_manifeste_sans_fin_est_refuse_au_plafond()
    {
        var corps = new SansFin();
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var r = await Flux(new Serveur(() => new StreamContent(corps))).GetManifestAsync(UpdateChannel.App, delai.Token);

        r.IsOk.Should().BeFalse();
        corps.Lus.Should().BeLessThan(HttpUpdateFeed.ManifesteOctetsMax + 128 * 1024, "rien n'est lu au-delà du plafond");
        delai.IsCancellationRequested.Should().BeFalse("le refus vient du plafond, pas du délai");
    }

    [Fact]
    public async Task Un_manifeste_annonce_trop_grand_n_est_pas_lu()
    {
        var corps = new SansFin();
        var contenu = new StreamContent(corps);
        contenu.Headers.ContentLength = 100L * 1024 * 1024;

        var r = await Flux(new Serveur(() => contenu)).GetManifestAsync(UpdateChannel.App, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        corps.Lus.Should().Be(0);
    }

    [Theory]
    [InlineData("http://updates.example.invalid/DrDXT-Setup.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("pas une adresse")]
    [InlineData("")]
    public async Task Une_charge_hors_https_est_refusee_sans_rien_demander(string url)
    {
        var serveur = new Serveur(() => new ByteArrayContent([1, 2, 3]));

        var r = await Flux(serveur).DownloadAsync(url, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        serveur.Demandes.Should().Be(0);
    }

    [Fact]
    public void Une_signature_demesuree_est_refusee_avant_d_etre_decodee()
    {
        var verificateur = UpdateVerifier.Create(UpdateDevKeys.UpdatePublicKey).Value!;
        var manifeste = new UpdateManifest(UpdateChannel.App, "1.4.0", "https://updates.example.invalid/x.exe",
            new string('0', 64), new string('A', 40 * 1024 * 1024));

        var avant = GC.GetAllocatedBytesForCurrentThread();
        var r = verificateur.VerifyManifest(manifeste);
        var alloue = GC.GetAllocatedBytesForCurrentThread() - avant;

        r.IsOk.Should().BeFalse();
        alloue.Should().BeLessThan(1024 * 1024, "une signature Ed25519 tient en 88 caractères : le reste n'est jamais décodé");
    }
}
