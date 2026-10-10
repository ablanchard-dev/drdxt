using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// D1 (10/10/2026) : sur une carte NVIDIA, le docteur nomme la surcouche DLSS et le mode « Ultra » comme des choix du
// joueur, sans chiffre promis ; sur une autre carte, rien.
public sealed class OptionsNvidiaDuJoueurTests
{
    private static GpuInfo Carte(string modele) => new(modele, modele.Contains("NVIDIA") ? "NVIDIA" : "AMD", 8L << 30, "617.42", TriState.On);

    [Fact]
    public void Sur_une_carte_NVIDIA_un_constat_d_information_qui_nomme_les_deux_options_et_le_guide()
    {
        var c = OptionsNvidiaDuJoueur.Juger([Carte("NVIDIA GeForce RTX 4060")]).Should().ContainSingle().Subject;

        c.Domaine.Should().Be("GPU DRIVER");
        c.Statut.Should().Be(RepairStatus.Observed, "une information, pas une panne");
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("DLSS").And.Contain("Ultra").And.Contain("5.19").And.Contain("latence");
        c.Explication.Should().NotMatchRegex(@"\d+\s*%", "aucun chiffre promis");
    }

    [Fact]
    public void Sans_carte_NVIDIA_rien()
    {
        OptionsNvidiaDuJoueur.Juger([Carte("AMD Radeon RX 6700 XT")]).Should().BeEmpty();
        OptionsNvidiaDuJoueur.Juger([]).Should().BeEmpty();
    }
}
