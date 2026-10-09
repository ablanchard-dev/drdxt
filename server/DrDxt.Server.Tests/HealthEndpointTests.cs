using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DrDxt.Server.Tests;

// -----------------------------------------------------------------------
// /health DIT CE QU'IL VOIT.
//
// Il disait « ok » en dur, une constante incapable de repondre « non ». Depuis le 23/09/2026 le
// serveur ne sert plus que le canal de mise a jour, et ce canal ne sert RIEN sans graine de
// signature (503 sur chaque manifeste). Le statut se deduit donc de cette graine.
// -----------------------------------------------------------------------
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static async Task<string> StatutAsync(WebApplicationFactory<Program> factory)
    {
        var response = await factory.CreateClient().GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task Sans_graine_de_signature_la_sante_dit_degraded()
    {
        var sansGraine = _factory.WithWebHostBuilder(b => b.UseSetting("ManifestSigning:Ed25519PrivateSeedBase64", ""));
        (await StatutAsync(sansGraine)).Should().Be("degraded",
            "le canal ne peut rien signer : un statut « ok » mentirait");
    }

    [Fact]
    public async Task Avec_une_graine_la_sante_dit_ok()
    {
        var graine = Convert.ToBase64String(new byte[32]);
        var avecGraine = _factory.WithWebHostBuilder(b => b.UseSetting("ManifestSigning:Ed25519PrivateSeedBase64", graine));
        (await StatutAsync(avecGraine)).Should().Be("ok");
    }
}
