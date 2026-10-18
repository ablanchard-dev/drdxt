using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;
using static DrDxt.Detection.Hardware.PeripheriquesDEntree;

namespace DrDxt.Detection.Tests.Hardware;

// La cadence qu'une manette USB demande au bus, avec l'arrondi du contrôleur, sans jugement.
public sealed class CadenceManetteAnnonceeTests
{
    // Notre règle : l'arrondi du contrôleur s'applique aussi en pleine vitesse. 10 ms annoncées sont servies toutes les 8 ms.
    [Theory]
    [InlineData(1, 10, 125)]
    [InlineData(1, 4, 250)]
    [InlineData(1, 1, 1000)]
    [InlineData(2, 1, 8000)]
    [InlineData(2, 4, 1000)]
    public void La_cadence_annoncee_suit_l_arrondi_du_controleur(int vitesse, int intervalle, int hz)
        => FrequenceSouris.Hz(vitesse, intervalle).Should().Be(hz);

    [Fact]
    public void Seules_les_manettes_USB_sont_lues_et_dites_sans_jugement()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        Appareil[] appareils =
        [
            new("Manette USB", @"USB\VID_1\1", [Role.Manette], Bus.Usb, 0, 1, 1),
            new("Manette BT", @"BTHENUM\2", [Role.Manette], Bus.Bluetooth, 0, 1, 2),
            new("Souris", @"USB\VID_3\3", [Role.Souris], Bus.Usb, 0, 1, 3),
        ];
        var c = FrequenceSouris.JugerManettes(appareils, _ => 250).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("toutes les 4 ms, jusqu'à 250 Hz");
        FrequenceSouris.JugerManettes(appareils, _ => null).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
}
