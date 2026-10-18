using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Une mesure de deux secondes qui ne dépend pas du repos (les lectures dures) commence dès la construction de l'audit et
// court pendant ses autres lectures, au lieu de s'ajouter à la fin du scan.
public sealed class MesureEnFondTests
{
    [Fact]
    public void La_mesure_commence_avant_qu_on_la_demande_et_rend_sa_valeur()
    {
        using var partie = new ManualResetEventSlim();
        var lire = RepairAuditService.EnFond<double?>(() => { partie.Set(); return 42; });

        partie.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("la mesure est lancée dès la construction");
        lire().Should().Be(42);
    }
}
