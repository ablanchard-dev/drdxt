using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La vraie demande d'alimentation de Windows : la structure passée à PowerCreateRequest ne se prouve qu'en l'appelant. La demande
// vit le temps du test, et Windows la retire avec le processus.
public sealed class SansVeilleTests
{
    [Fact]
    public void La_demande_se_pose_puis_se_retire()
    {
        var veille = SansVeille.Ouvrir();
        veille.Active.Should().BeTrue("Windows accepte la demande « système requis »");

        veille.Dispose();
        veille.Active.Should().BeFalse();
        veille.Dispose();
    }
}
