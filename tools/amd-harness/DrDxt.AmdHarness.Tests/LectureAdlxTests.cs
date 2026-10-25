using DrDxt.AmdHarness.Reporting;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;
using Xunit;

namespace DrDxt.AmdHarness.Tests;

/// <summary>« --lire » montre chaque réglage que DrDXT écrit sur une carte AMD, pour qu'un testeur le compare à AMD
/// Software : la Vsync du pilote comprise, que le plafond VRR met « toujours activée ».</summary>
public sealed class LectureAdlxTests
{
    [Theory]
    [InlineData(0, "toujours coupée")]
    [InlineData(1, "coupée sauf avis du jeu")]
    [InlineData(2, "activée sauf avis du jeu")]
    [InlineData(3, "toujours activée")]
    public void La_vsync_du_pilote_a_sa_ligne(int mode, string attendu)
        => LectureAdlx.Lignes(new AdlxReglagesLus(VsyncMode: mode), true).Should().Contain($"Vsync         : {attendu}");

    [Fact]
    public void Une_vsync_non_lue_le_dit()
        => LectureAdlx.Lignes(new AdlxReglagesLus(), true).Should().Contain("Vsync         : non lue");

    // La limite de puissance et sa plage, telles qu'ADLX les rend : c'est ce qui dira si l'unité est un % du TDP ou un
    // décalage autour de 0, le jour où un testeur AMD les met à côté d'AMD Software.
    [Fact]
    public void La_limite_de_puissance_se_dit_avec_sa_plage_et_sinon_non_lue()
    {
        LectureAdlx.LignesPuissance([(1, new AdlxPowerReads { PowerLimitPct = 0, PowerRange = new AdlxIntRange { Min = -10, Max = 15, Step = 1 } })])
            .Should().ContainSingle().Which.Should().Be("Puissance (carte 1) : limite 0, plage -10 à 15, pas 1");
        LectureAdlx.LignesPuissance([]).Should().Equal("Puissance     : non lue");
    }
}
