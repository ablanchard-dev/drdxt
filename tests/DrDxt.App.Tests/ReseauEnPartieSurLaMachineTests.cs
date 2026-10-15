using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La vraie mesure du relevé, dix pings vers la box et dix vers 1.1.1.1.
public sealed class ReseauEnPartieSurLaMachineTests
{
    [Fact, Trait("Category", "Machine")] // pingue la vraie box et internet depuis cette machine
    public async Task La_box_et_internet_repondent_depuis_cette_machine()
    {
        var (box, internet, pertes) = await MesureAvantPartie.ReseauAsync(CancellationToken.None);
        Console.WriteLine($"box {box} ms, internet {internet} ms, pertes {pertes} %");
        internet.Should().NotBeNull();
        pertes.Should().NotBeNull();
        box.Should().NotBeNull();
    }
}
