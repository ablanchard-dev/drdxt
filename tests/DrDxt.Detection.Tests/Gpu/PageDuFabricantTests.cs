using System.Net;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Les pages de NVIDIA et d'AMD se lisent avec un plafond d'octets : une page captive ou un incident qui rendrait des Mio n'est
// pas chargé en mémoire, la version reste non lue.
public sealed class PageDuFabricantTests
{
    private sealed class Reponse(int octets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[octets]) });
    }

    [Fact]
    public async Task Une_page_au_dela_du_plafond_n_est_pas_lue()
    {
        using var http = PageDuFabricant.Client(new Reponse(PageDuFabricant.OctetsMax + 1));

        await FluentActions.Awaiting(() => http.GetStringAsync("https://exemple.invalid/")).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Une_page_ordinaire_se_lit()
    {
        using var http = PageDuFabricant.Client(new Reponse(25_000));

        (await http.GetStringAsync("https://exemple.invalid/")).Should().HaveLength(25_000);
    }
}
