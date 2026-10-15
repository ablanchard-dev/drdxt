using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>L'incident graphique se reconnaissait à « carte graphique » dans l'explication : en anglais (« graphics card »),
/// il ne l'était plus. Il se reconnaît au code d'arrêt, la même liste que le docteur.</summary>
public sealed class IncidentGraphiqueReconnuTests
{
    [Theory]
    [InlineData("It comes from the graphics card and its driver.")]
    [InlineData("Il vient de la carte graphique et de son pilote.")]
    public void L_incident_graphique_se_reconnait_au_code_d_arret_dans_toute_langue(string explication)
        => RepairFindingViewModel.TitreAffiche(new RepairFinding("STABILITE", "incident noyau conserve",
                "3x 0x193 VIDEO_DXGKRNL_LIVEDUMP", null, RepairStatus.Observed, explication))
            .Should().Be("Incident du pilote graphique, conservé");

    [Fact]
    public void Un_autre_code_n_est_pas_graphique()
        => RepairFindingViewModel.TitreAffiche(new RepairFinding("STABILITE", "incident noyau conserve",
                "1x 0x15F CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", null, RepairStatus.Observed, "graphics card"))
            .Should().NotBe("Incident du pilote graphique, conservé");
}
