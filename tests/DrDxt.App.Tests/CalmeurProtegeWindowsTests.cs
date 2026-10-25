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

    // « Jamais un logiciel de sécurité » : Defender ne suffit pas, les antivirus courants aussi, par le nom de leur processus.
    [Theory]
    [InlineData("AvastSvc")]
    [InlineData("AVGUI")]
    [InlineData("avp")]
    [InlineData("ekrn")]
    [InlineData("bdagent")]
    [InlineData("vsserv")]
    [InlineData("MBAMService")]
    [InlineData("mcshield")]
    [InlineData("NortonSecurity")]
    [InlineData("SophosHealth")]
    [InlineData("Avira.ServiceHost")]
    [InlineData("mbamtray")]
    [InlineData("avguard")]
    public void Un_antivirus_courant_est_protege(string nom) => CalmeurDeVoisins.Protege(nom).Should().BeTrue();

    [Fact]
    public void Un_programme_du_joueur_reste_proposable()
    {
        CalmeurDeVoisins.Protege("Discord").Should().BeFalse();
        CalmeurDeVoisins.Protege("Steam").Should().BeFalse("témoin : un nom de marque ne prend pas un programme voisin");
        // Une marque en tête d'un autre mot n'est pas cette marque : « esetup » n'est pas ESET.
        CalmeurDeVoisins.Protege("esetup").Should().BeFalse();
        CalmeurDeVoisins.Protege("Nortonville").Should().BeFalse();
        CalmeurDeVoisins.AProposer(
            [new DrDxt.Application.Parties.PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 1, 100, 80,
                DrDxt.Core.BottleneckState.GpuBound, Voisins: [new("WmiPrvSE", 40, 2), new("Discord", 30, 2)])], [])
            .Should().Equal("Discord");
    }
}
