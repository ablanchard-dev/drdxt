using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// Le microcode des Core de 13e et 14e génération de bureau, avant tout conseil de puissance.
public sealed class MicrocodeRaptorLakeTests
{
    private const string Rpl = "Intel64 Family 6 Model 183 Stepping 1";   // relevé sur la machine de référence

    // Un i5-13400 sur puce Alder Lake (autre modèle, microcode 0x3x) n'est pas concerné.
    [Fact]
    public void Un_i5_13400_sur_puce_Alder_Lake_n_est_pas_accuse()
        => MicrocodeRaptorLake.Juger("13th Gen Intel(R) Core(TM) i5-13400", 0x36, "Intel64 Family 6 Model 151 Stepping 5").Should().BeEmpty();

    [Fact]
    public void Sans_identifiant_rien_n_est_dit()
        => MicrocodeRaptorLake.Juger("Intel(R) Core(TM) i7-14700F", 0x100, null).Should().BeEmpty();

    [Fact]
    public void Un_microcode_recent_est_dit_a_jour()
    {
        var c = MicrocodeRaptorLake.Juger("Intel(R) Core(TM) i7-14700F", 0x133, Rpl).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.ValeurCourante.Should().Be("0x133");
    }

    [Theory]
    [InlineData(0x12B)]
    [InlineData(0x11F)]
    public void Un_microcode_d_avant_le_correctif_demande_la_mise_a_jour_du_BIOS(uint rev)
        => MicrocodeRaptorLake.Juger("13th Gen Intel(R) Core(TM) i9-13900K", rev, Rpl).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Suspicious);

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-13700H")]      // portable
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor")]
    [InlineData(null)]
    public void Hors_Core_de_bureau_13e_et_14e_generation_rien_n_est_dit(string? nom)
        => MicrocodeRaptorLake.Juger(nom, 0x100, Rpl).Should().BeEmpty();

    [Fact]
    public void Une_revision_non_lue_ne_dit_rien()
        => MicrocodeRaptorLake.Juger("Intel(R) Core(TM) i7-14700F", null, Rpl).Should().BeEmpty();

    [Fact]
    public void La_lecture_reelle_ne_leve_pas()
    {
        var act = () => MicrocodeRaptorLake.Lire();
        act.Should().NotThrow();
    }
}
