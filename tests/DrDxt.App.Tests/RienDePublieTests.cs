using System.IO;
using System.Net;
using System.Net.Http;
using DrDxt.App.Updates;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Un 404 dit « rien de publié à cette adresse », vrai que le canal soit sain ou cassé : il ne dit pas « à jour ». « À jour »
/// est réservé au manifeste signé lu qui ne propose rien de plus récent, ou au 204 du serveur (canal existant, rien publié).
/// </summary>
public sealed class RienDePublieTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-rien-publie-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_racine, true); } catch (IOException) { } }

    private sealed class Serveur(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code));
    }

    private async Task<EtatMiseAJour> Verifier(HttpStatusCode code)
    {
        Directory.CreateDirectory(_racine);
        using var client = new HttpClient(new Serveur(code));
        var r = await VerificationMiseAJour.VerifierAsync(
            () => UpdateComposition.TryCreate("https://u.test", client, _racine, "1.0.0", UpdateDevKeys.CreateUpdateVerifier().Value!),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        return r.Etat;
    }

    [Fact]
    public async Task Un_404_se_dit_rien_de_publie_jamais_a_jour()
    {
        (await Verifier(HttpStatusCode.NotFound)).Should().Be(EtatMiseAJour.RienDePublie);
        VerificationMiseAJour.CleResx(EtatMiseAJour.RienDePublie).Should().Be("UI.Footer.Updates.NothingPublished");
    }

    [Fact]
    public async Task Un_204_reste_a_jour()
        => (await Verifier(HttpStatusCode.NoContent)).Should().Be(EtatMiseAJour.AJour);

    [Fact]
    public async Task Une_autre_erreur_reste_injoignable()
        => (await Verifier(HttpStatusCode.BadGateway)).Should().Be(EtatMiseAJour.Injoignable);
}
