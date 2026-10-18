using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>L'incident USB 3 se reconnaissait à « USB 3 » dans la valeur, présent par hasard dans les deux langues. Il se
/// reconnaît au code d'arrêt 0x144 (BUGCODE_USB3_DRIVER), comme l'incident graphique.</summary>
public sealed class IncidentUsb3ReconnuTests
{
    private static string Titre(string valeur) => RepairFindingViewModel.TitreAffiche(
        new RepairFinding("STABILITE", "incident noyau conserve", valeur, null, RepairStatus.Observed, "x"));

    [Theory]
    [InlineData("2x 0x144 BUGCODE_USB3_DRIVER, paramètre 1 0x1009, contrôleur USB 3 (vidage à chaud)")]
    [InlineData("2x 0x144 BUGCODE_USB3_DRIVER, parameter 1 0x1009")]
    public void L_incident_usb3_se_reconnait_au_code_d_arret_sans_le_texte(string valeur)
        => Titre(valeur).Should().Be(Docteur.T("RF.IncidentUsb3"));

    [Fact]
    public void Un_autre_code_n_est_pas_usb3()
        => Titre("1x 0x15F CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP, USB 3").Should().NotBe(Docteur.T("RF.IncidentUsb3"));
}
