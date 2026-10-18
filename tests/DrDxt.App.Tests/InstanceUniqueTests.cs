using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Un second lancement ne fait pas une seconde App : il réveille la première et rend la main.</summary>
public sealed class InstanceUniqueTests
{
    [Fact]
    public void Le_second_lancement_reveille_le_premier_et_ne_prend_pas_la_place()
    {
        var nom = @"Local\DrDXT-Test-" + Guid.NewGuid().ToString("N");
        using var reveille = new ManualResetEventSlim();
        using var premiere = InstanceUnique.Prendre(nom, reveille.Set);
        premiere.Should().NotBeNull();

        InstanceUnique.Prendre(nom, () => { }).Should().BeNull();
        reveille.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("la première App est priée de se montrer");
    }

    [Fact]
    public void La_place_se_libere_quand_la_premiere_se_ferme()
    {
        var nom = @"Local\DrDXT-Test-" + Guid.NewGuid().ToString("N");
        InstanceUnique.Prendre(nom, () => { })!.Dispose();
        using var suivante = InstanceUnique.Prendre(nom, () => { });
        suivante.Should().NotBeNull();
    }
}
