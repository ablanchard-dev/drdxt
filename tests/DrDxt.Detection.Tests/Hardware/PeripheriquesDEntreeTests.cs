using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;
using static DrDxt.Detection.Hardware.PeripheriquesDEntree;

namespace DrDxt.Detection.Tests.Hardware;

// L'inventaire des périphériques d'entrée : le rôle déclaré par HID, la liaison lue dans l'arbre.
public sealed class PeripheriquesDEntreeTests
{
    [Theory]
    [InlineData(0x01, 0x02, Role.Souris)]
    [InlineData(0x01, 0x06, Role.Clavier)]
    [InlineData(0x01, 0x05, Role.Manette)]
    [InlineData(0x01, 0x04, Role.Manette)]
    [InlineData(0x0C, 0x01, Role.Multimedia)]
    [InlineData(0xFF00, 0x01, Role.Autre)]
    public void Le_role_vient_de_la_page_et_de_l_usage_declares(int page, int usage, Role role)
        => RoleDe((ushort)page, (ushort)usage).Should().Be(role);

    // Notre règle : le fabricant ne fait jamais le rôle. Un casque ou un éclairage d'un fabricant de souris reste « autre ».
    [Fact]
    public void Un_appareil_d_un_fabricant_de_souris_qui_ne_declare_pas_de_souris_n_est_pas_une_souris()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = Juger([new("Casque", @"USB\VID_046D&PID_0AAA\1", [Role.Multimedia, Role.Autre], Bus.Usb, 0, 0x046D, 0x0AAA)]);
        c.ValeurCourante.Should().Be("0");
        c.Explication.Should().NotContain("Casque");
    }

    // Chaîne relevée sur la machine de référence : la souris derrière un concentrateur USB.
    [Fact]
    public void La_liaison_compte_les_concentrateurs_entre_l_appareil_et_le_port()
    {
        string[] chaine =
        [
            @"HID\VID_0738&PID_1703\7&D18AFE7&1&0000", @"USB\VID_0738&PID_1703\6&34110B8C&0&1", @"USB\VID_05E3&PID_0608\5&232B4DD6&0&5",
            @"USB\ROOT_HUB30\4&10EA92FE&0&0", @"PCI\VEN_8086&DEV_7A60\3",
        ];
        Liaison(chaine, n => n.StartsWith(@"USB\VID_05E3") ? "USBHUB3" : null)
            .Should().Be((@"USB\VID_0738&PID_1703\6&34110B8C&0&1", Bus.Usb, 1));
    }

    [Fact]
    public void Une_interface_d_un_appareil_composite_se_range_sous_l_appareil_et_le_Bluetooth_est_reconnu()
    {
        Liaison([@"HID\VID_046D&PID_C34A&MI_01&COL01\8", @"USB\VID_046D&PID_C34A&MI_01\7", @"USB\VID_046D&PID_C34A\6", @"USB\ROOT_HUB30\4"], _ => null)!
            .Value.Noeud.Should().Be(@"USB\VID_046D&PID_C34A\6");
        Liaison([@"HID\{00001124}_VID&0002054C\9", @"BTHENUM\{00001124}_VID&0002054C\8", @"BTH\MS_BTHBRB\6"], _ => null)!
            .Value.Liaison.Should().Be(Bus.Bluetooth);
    }

    [Fact]
    public void Le_constat_dit_chaque_appareil_de_jeu_avec_sa_liaison()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = Juger([new("Souris USB X", "n", [Role.Souris], Bus.Usb, 1, 0x0738, 0x1703),
            new("Manette", "m", [Role.Manette], Bus.Bluetooth, 0, 1, 2)]);
        c.Explication.Should().Contain("« Souris USB X » : souris, en USB, derrière 1 concentrateur(s).")
            .And.Contain("manette, en Bluetooth");
        Juger(null).Statut.Should().Be(RepairStatus.Unknown);
    }

    // Un nom de service se compare sans la casse : « usbhub3 » est le même concentrateur que « USBHUB3 ».
    [Fact]
    public void Un_concentrateur_se_reconnait_quelle_que_soit_la_casse_de_son_service()
    {
        string[] chaine = [@"HID\VID_0738&PID_1703\1", @"USB\VID_0738&PID_1703\2", @"USB\VID_05E3&PID_0608\3", @"USB\ROOT_HUB30\4", @"PCI\VEN_8086&DEV_7A60\5"];
        Liaison(chaine, n => n.StartsWith(@"USB\VID_05E3") ? "usbhub3" : null)!.Value.Concentrateurs.Should().Be(1);
    }
}
