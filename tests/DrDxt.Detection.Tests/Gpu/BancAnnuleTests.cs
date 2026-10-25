using DrDxt.Detection.Gpu.Bench;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Une charge coupée par le délai de la pose n'a pas tenu sa durée : elle ne prouve rien, et le décalage de tension ne se
// garde que sur une passe saine complète.
public sealed class BancAnnuleTests
{
    [Fact]
    public void Une_charge_annulee_n_est_pas_complete()
    {
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var (lancees, complete) = VorticeGpuComputeRunner.Charger(() => { passes++; cts.Cancel(); }, TimeSpan.FromMinutes(1), cts.Token);
        complete.Should().BeFalse();
        lancees.Should().Be(1);
        passes.Should().Be(1);
    }

    [Fact]
    public void Une_charge_menee_a_son_terme_est_complete()
    {
        var (lancees, complete) = VorticeGpuComputeRunner.Charger(() => { }, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        complete.Should().BeTrue();
        lancees.Should().BeGreaterThan(0);
    }
}
