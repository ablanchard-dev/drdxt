using DrDxt.AmdHarness.Reporting;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.AmdHarness.Tests;

/// <summary>« --lire » montre aussi ce que DrDXT lit d'un Ryzen par PawnIO, pour qu'un testeur le compare à HWiNFO et à
/// ZenTimings : chaque lecture a sa ligne, et ce qui n'est pas lu le dit.</summary>
public sealed class LecturePawnIoAmdTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

    [Fact]
    public void Sans_PawnIO_une_seule_ligne_le_dit()
        => LecturePawnIoAmd.Lignes(false, Zen4, 50, [48], 0, (1, 2), []).Should().ContainSingle().Which.Should().Contain("absent");

    [Fact]
    public void Chaque_lecture_a_sa_ligne()
    {
        var canal = TimingsMemoireAmd.Registres.ToDictionary(r => r, r => 0x11223344u);

        var l = LecturePawnIoAmd.Lignes(true, Zen4, 52.25, [50, 51.5], 1u << 1, (0x4C, 0x00544500), [canal]);

        l.Should().Contain("Génération    : Zen 4")
            .And.Contain("Tctl          : 52.3 °C")
            .And.Contain("CCD           : 50.0 °C ; 51.5 °C")
            .And.Contain("S5 (reset)    : 0x00000002, bits 1")
            .And.Contain("SMU           : version 0x00544500, nom de code 76");
        l.Should().ContainSingle(x => x.StartsWith("Timings canal 0 : ", StringComparison.Ordinal))
            .Which.Should().Contain($"{TimingsMemoireAmd.Registres[0]:X5}=11223344");
    }

    [Fact]
    public void Ce_qui_n_est_pas_lu_le_dit()
    {
        var l = LecturePawnIoAmd.Lignes(true, null, null, null, uint.MaxValue, null, null);

        l.Should().Contain("Génération    : non reconnue").And.Contain("Tctl          : non lue").And.Contain("CCD           : non lus")
            .And.Contain("S5 (reset)    : non lu (tous les bits à 1)").And.Contain("SMU           : non lu").And.Contain("Timings       : non lus");
    }
}
