using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// NvApiTuningActions : ce qui se prouve sans carte graphique.
//
// La frontière native NVAPI (la recette SetPstates20 / ClientPowerPoliciesSetStatus issue
// de la rétro-ingénierie, les tailles de structures interop, la recherche de l'horloge P0
// et les conversions MHz/%) vit dans DrDxt.GpuNative (NvApiNative) et est figée par
// DrDxt.GpuNative.Tests. Reste testable ici l'orchestrateur et la politique côté helper :
//   1. Contrat d'échec propre : les Get* en lecture seule ne lèvent jamais et renvoient un
//      Result ; sur une machine sans NVAPI ils échouent proprement (HELPER.OP_FAILED). Sûr
//      sur une vraie machine NVIDIA (lecture seule).
//   2. Rails de politique : les bandes prudentes de décalage et de puissance (NvApiTuningMath)
//      que l'écriture élevée revérifie avant d'atteindre la frontière native.
// SetCoreClockOffset et SetPowerLimitPercent ne sont pas appelés ici : un test unitaire
// n'écrit jamais un delta d'horloge ou de puissance sur une vraie carte.
// -----------------------------------------------------------------------
public sealed class NvApiTuningActionsContractTests
{
    [Fact]
    public void GetCoreClockOffsetMhz_never_throws_and_returns_a_result()
    {
        var tuning = new NvApiTuningActions();

        var act = () => tuning.GetCoreClockOffsetMhz(0);

        var result = act.Should().NotThrow(
            "the NVAPI read boundary must fail-clean, never throw across the IPC").Subject;
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetPowerLimitPercent_never_throws_and_returns_a_result()
    {
        var tuning = new NvApiTuningActions();

        var act = () => tuning.GetPowerLimitPercent(0);

        var result = act.Should().NotThrow().Subject;
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetCoreClockOffsetMhz_on_a_box_without_nvapi_fails_clean()
    {
        // Runners CI et postes sans NVIDIA : le chemin attendu est un HELPER.OP_FAILED propre, jamais
        // une exception. Gardé pour rester juste sur une machine qui a bien un pilote NVIDIA (là, la
        // lecture peut légitimement réussir ; seul le profil d'échec propre sans NVIDIA est figé).
        var result = new NvApiTuningActions().GetCoreClockOffsetMhz(0);

        if (!result.IsOk)
        {
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            result.Error!.Value.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetCoreClockOffsetMhz_rejects_a_negative_gpu_index_clean()
    {
        var result = new NvApiTuningActions().GetCoreClockOffsetMhz(-1);

        result.IsOk.Should().BeFalse("a negative GPU index is never valid");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }
}

public sealed class NvApiTuningRailTests
{
    // Les rails durs sont purs et ne doivent jamais lever : int.MinValue (pas de Math.Abs !)
    // et NaN (toute comparaison avec NaN est fausse) sont les deux pièges.
    [Fact]
    public void Core_offset_rail_accepts_a_normal_undervolt_offset() =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(-120).Should().BeTrue();

    [Fact]
    public void Core_offset_rail_rejects_int_min_value_without_throwing()
    {
        var act = () => NvApiTuningMath.IsCoreOffsetWithinHardRail(int.MinValue);

        act.Should().NotThrow("the rail must never overflow on int.MinValue")
           .Subject.Should().BeFalse();
    }

    [Fact]
    public void Core_offset_rail_rejects_an_absurd_offset() =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(5000).Should().BeFalse();

    [Fact]
    public void Power_rail_accepts_a_normal_percent() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(90).Should().BeTrue();

    [Fact]
    public void Power_rail_rejects_nan() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(double.NaN).Should().BeFalse();

    [Fact]
    public void Power_rail_rejects_infinity() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(double.PositiveInfinity).Should().BeFalse();

    [Fact]
    public void Power_rail_rejects_an_absurd_percent() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(500).Should().BeFalse();

    // La borne BASSE n'etait tenue par aucun test. C'est elle qui borne ce qu'un op eleve peut
    // faire de la limite de puissance (l'allowlist accepte 0 a 100 %) : ramenee a 0, rien ne l'aurait vu.
    [Theory]
    [InlineData(49.9, false)]
    [InlineData(50.0, true)]
    [InlineData(120.0, true)]
    [InlineData(120.1, false)]
    public void Power_rail_bounds_are_exact(double percent, bool accepted) =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(percent).Should().Be(accepted);
}
