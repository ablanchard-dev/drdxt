using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>
/// Le panneau de sante affichait les cles internes (« STABILITE », « GPU DRIVER »,
/// « POWER »). La cle reste celle du journal, sinon deux photos ne se comparent plus ; seul
/// l'affichage change.
/// </summary>
public sealed class RepairDomaineLibelleTests
{
    [Theory]
    [InlineData("STABILITE", "Stabilité")]
    [InlineData("POWER", "Alimentation")]
    [InlineData("GPU DRIVER", "Pilote graphique")]
    [InlineData("BCD", "Démarrage de Windows")]
    [InlineData("INCONNU", "INCONNU")]
    public void LeDomaineSAfficheEnMots(string cle, string attendu)
    {
        var vm = new RepairFindingViewModel(
            new RepairFinding(cle, "x", "lue", null, RepairStatus.Observed, "parce que."));

        vm.Domaine.Should().Be(attendu);
    }

    // Meme regle pour le NOM du constat, cle du journal ecrite sans accents.
    [Theory]
    [InlineData("arrets brutaux", "Arrêts brutaux")]
    [InlineData("incident noyau conserve", "Incident noyau conservé")]
    [InlineData("frequence effective", "Fréquence effective")]
    [InlineData("test memoire Windows", "Test mémoire Windows")]
    [InlineData("usure · Samsung SSD 990 PRO 2TB", "Usure · Samsung SSD 990 PRO 2TB")]
    [InlineData("disabledynamictick", "Minuterie dynamique coupée")]
    [InlineData("bootstatuspolicy", "Écran de récupération au démarrage")]
    [InlineData("SystemResponsiveness", "Part du processeur réservée à l'arrière-plan")]
    [InlineData("Games\\Priority", "Priorité processeur des jeux")]
    [InlineData("Samsung SSD", "Samsung SSD")]
    public void LeNomSAfficheAvecSesAccents(string cle, string attendu)
    {
        var vm = new RepairFindingViewModel(
            new RepairFinding("STABILITE", cle, "lue", null, RepairStatus.Observed, "parce que."));

        vm.Nom.Should().Be(attendu);
    }

    [Fact]
    public void LActionSAfficheAvecSesAccents()
    {
        var vm = new RepairFindingViewModel(new RepairFinding(
            "BCD", "x", "lue", null, RepairStatus.Suspicious, "parce que.", RepairAction.GuidedBios));

        vm.ActionLabel.Should().Be("passage guidé par le BIOS");
    }
}
