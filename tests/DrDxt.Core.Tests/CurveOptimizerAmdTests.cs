using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// 07/10/2026 (A7) : Curve Optimizer, guide BIOS pour un Ryzen de bureau 5000 et plus ; jamais écrit.
public sealed class CurveOptimizerAmdTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", true)]
    [InlineData("AMD Ryzen 5 9600X 6-Core Processor", true)]
    [InlineData("AMD Ryzen 5 5600", true)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", false)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", false)]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-14700F", false)]
    [InlineData(null, false)]
    public void Seul_un_ryzen_de_bureau_5000_et_plus_recoit_le_guide(string? cpu, bool guide)
        => (CurveOptimizerAmd.Juger(cpu, "ASUSTeK COMPUTER INC.") is not null).Should().Be(guide);

    [Fact]
    public void Le_chemin_n_est_donne_que_sur_une_carte_msi_et_la_source_et_le_risque_sont_dits()
    {
        var msi = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "Micro-Star International Co., Ltd.")!;
        msi.Explication.Should().Contain("Precision Boost Overdrive").And.Contain("Click BIOS X").And.Contain("instabilité")
            .And.Contain("n'écrit jamais dans le BIOS").And.Contain("n'est pas mesuré");
        msi.Statut.Should().Be(RepairStatus.Observed);

        CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "Gigabyte Technology Co., Ltd.")!.Explication
            .Should().NotContain("Click BIOS X").And.Contain("manuel de la carte");
    }
}
