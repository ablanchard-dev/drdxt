using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>UI1 (08/10/2026, vu sur capture de la page Windows) : un réglage défait depuis portait son identifiant
/// (« Latency.vrrFlipModelOn ») pour titre ; il porte le nom que DrDXT lui donne partout ailleurs.</summary>
public sealed class TitreDUnReglageDefaitTests
{
    private static RepairFindingViewModel Defait(string id) => new(new RepairFinding("PERSISTANCE", id, "revenu à la valeur d'avant",
        "la valeur posée par DrDXT", RepairStatus.Suspicious, "défait", RepairAction.Recommandation));

    [Fact]
    public void Un_reglage_defait_porte_son_nom()
        => ScanViewModel.TitreDuConstat(Defait("latency.vrrFlipModelOn"), k => k == "tweak.latency.vrrFlipModelOn.title" ? "Mode fenêtré optimisé" : k)
            .Should().Be("Mode fenêtré optimisé");

    [Fact]
    public void Un_identifiant_inconnu_garde_le_libelle_d_avant()
        => ScanViewModel.TitreDuConstat(Defait("nouveau.reglage"), k => "jamais appelé").Should().NotBe("jamais appelé");
}
