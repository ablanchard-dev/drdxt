using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Vu sur capture (08/10/2026) : la proposition « faire passer après le jeu » listait WmiPrvSE, un processus de
/// Windows, sous le texte « toujours protégés : … Windows ». Les hôtes de Windows qui travaillent en arrière-plan sont
/// protégés comme le reste du système.</summary>
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
