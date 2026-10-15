using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Les hôtes de Windows qui travaillent en arrière-plan (WmiPrvSE…) sont protégés comme le reste du système :
/// sans ce test, « faire passer après le jeu » pourrait lister un processus de Windows sous le texte « toujours
/// protégés : … Windows ».</summary>
public sealed class CalmeurProtegeWindowsTests
{
    [Theory]
    [InlineData("WmiPrvSE")]
    [InlineData("SearchIndexer")]
    [InlineData("TiWorker")]
    [InlineData("TrustedInstaller")]
    [InlineData("MoUsoCoreWorker")]
    [InlineData("RuntimeBroker")]
    [InlineData("spoolsv")]
    public void Un_hote_de_Windows_est_protege(string nom) => CalmeurDeVoisins.Protege(nom).Should().BeTrue();

    [Fact]
    public void Un_programme_du_joueur_reste_proposable()
    {
        CalmeurDeVoisins.Protege("Discord").Should().BeFalse();
        CalmeurDeVoisins.AProposer(
            [new DrDxt.Application.Parties.PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 1, 100, 80,
                DrDxt.Core.BottleneckState.GpuBound, Voisins: [new("WmiPrvSE", 40, 2), new("Discord", 30, 2)])], [])
            .Should().Equal("Discord");
    }
}
