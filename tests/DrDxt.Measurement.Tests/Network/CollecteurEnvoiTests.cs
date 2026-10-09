using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

public class CollecteurEnvoiTests
{
    private sealed class PingFixe(double? ms) : INetworkProbe
    {
        public Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new NetworkProbeSample(ms));
    }

    [Fact]
    public async Task L_envoi_se_calcule_depuis_le_compteur_et_un_compteur_qui_recule_ne_fabrique_rien()
    {
        // Un envoi de 100 Mbit/s tenu dans le temps reel, puis le compteur retombe (carte relancee).
        // 25/09/2026 : le compteur avancait de 3 125 000 octets par appel, en supposant qu'un pas
        // de 250 ms dure 250 ms. Sous la charge de la suite complete, il en durait 325 a 360 : le produit
        // divisait, a juste titre, par le temps reellement ecoule, et le test echouait 3 fois sur 5.
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var appels = 0;
        long Octets() => ++appels >= 4 ? 10 : (long)(chrono.Elapsed.TotalSeconds * 12_500_000);
        var c = new CollecteurEnvoi(new PingFixe(9), "cible", Octets);

        // 26/09/2026 : la fenetre de 760 ms n'en tenait que deux pas sous la charge d'une revue (clone frais,
        // suite complete) ; le troisieme echantillon, celui du compteur qui recule, manquait. Trois secondes :
        // il faudrait qu'un pas depasse une seconde pour qu'il manque.
        // 27/09/2026 : en CI, deux pas seulement en trois secondes (runner charge). Huit secondes.
        var e = await c.CollecterAsync(TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        e.Should().HaveCountGreaterThanOrEqualTo(3);
        e[0].EnvoiMbps.Should().BeInRange(90, 110, "100 Mbit/s tenus, quelle que soit la duree reelle du pas");
        e[2].EnvoiMbps.Should().Be(0, "un compteur qui recule n'est pas un envoi negatif");
        e.Should().OnlyContain(x => x.PingMs == 9);
    }
}
