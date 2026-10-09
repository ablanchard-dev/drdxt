using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// 04/10/2026 (plan 2.1) : l'Instant Replay de NVIDIA, lu dans les réglages de l'overlay (forme relevée sur la machine
// de référence, ShareSettings.json du 07/07/2026).
public sealed class EnregistrementNvidiaTests
{
    private const string Allume = """{"settings":{"globalhighlights":{"enabled":true},"video":{"irEnabled":true}}}""";

    [Fact]
    public void Instant_Replay_allume_se_dit_a_regarder_avec_le_moyen_de_le_juger()
    {
        var c = EnregistrementNvidia.Juger(Allume).Should().ContainSingle().Subject;
        c.Domaine.Should().Be("GPU DRIVER");
        c.Statut.Should().Be(RepairStatus.UserChoiceProbable, "c'est un choix du joueur, pas une panne");
        c.Explication.Should().Contain("5 %").And.Contain("Alt+Z").And.Contain("parties");
    }

    [Theory]
    [InlineData("""{"settings":{"video":{"irEnabled":false}}}""")]
    [InlineData("""{"settings":{"video":{}}}""")]
    [InlineData("pas du json")]
    [InlineData(null)]
    public void Eteint_absent_ou_illisible_ne_dit_rien(string? json)
        => EnregistrementNvidia.Juger(json).Should().BeEmpty();

    [Fact]
    public void Le_fichier_reel_se_lit_sans_erreur_s_il_existe()
    {
        var json = EnregistrementNvidia.LireReglages();
        if (json is null) return; // machine sans overlay NVIDIA
        var act = () => EnregistrementNvidia.Juger(json).ToList();
        act.Should().NotThrow();
    }

    // UI1 (critique du 08/10, défaut 11) : la ligne visible disait « Instant Replay allumé » ; le coût, qui justifie le point
    // orange, n'était que derrière le « ? ». La valeur le porte, avec sa source.
    [Fact]
    public void La_valeur_visible_dit_le_cout_publie()
        => EnregistrementNvidia.Juger(Allume).Single().ValeurCourante.Should().Contain("5 %").And.Contain("NVIDIA");
}
