using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// 06/10/2026 (chantier B5 b) : la fréquence d'interrogation qu'une souris USB annonce (bInterval de son point d'accès
// d'interruption, USB 2.0 §9.6.6). C'est un maximum : au-dessous de 125 Hz le défaut est certain, au-dessus on dit « jusqu'à ».
public sealed class FrequenceSourisTests
{
    [Theory]
    [InlineData(1, 1, 1000)]   // pleine vitesse, 1 ms
    [InlineData(1, 8, 125)]    // pleine vitesse, 8 ms
    // Revue T3+T4 (F-26) : le contrôleur (xHCI 6.2.3.6, EHCI pareil) arrondit l'intervalle en basse et pleine vitesse
    // à la puissance de 2 inférieure : 10 ms annoncées = 8 ms servies.
    [InlineData(0, 10, 125)]   // basse vitesse, 10 ms ⇒ 8 ms
    [InlineData(1, 9, 125)]    // pleine vitesse, 9 ms ⇒ 8 ms (et non 111 Hz)
    [InlineData(1, 3, 500)]    // 3 ms ⇒ 2 ms
    [InlineData(2, 1, 8000)]   // haute vitesse, 1 micro-trame
    [InlineData(2, 4, 1000)]   // haute vitesse, 2^3 × 125 µs = 1 ms
    public void La_frequence_suit_la_vitesse_du_bus_et_l_intervalle(int vitesse, int intervalle, int hz)
        => FrequenceSouris.Hz(vitesse, intervalle).Should().Be(hz);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 17)]
    [InlineData(7, 1)]
    public void Un_intervalle_ou_une_vitesse_hors_norme_n_est_pas_lu(int vitesse, int intervalle)
        => FrequenceSouris.Hz(vitesse, intervalle).Should().BeNull();

    // USB_NODE_CONNECTION_INFORMATION_EX, empaqueté sur 1 octet : 35 octets d'en-tête, puis USB_PIPE_INFO (descripteur de
    // point d'accès de 7 octets + 4).
    [Fact]
    public void Les_intervalles_des_points_d_acces_d_interruption_entrants_se_lisent()
    {
        var info = new byte[35 + 2 * 11];
        info[23] = 1; // pleine vitesse
        BitConverter.GetBytes(2).CopyTo(info, 27); // deux tuyaux ouverts
        // Tuyau 1 : interruption, sortant (0x02) : ignoré.
        new byte[] { 7, 5, 0x02, 0x03, 8, 0, 4 }.CopyTo(info, 35);
        // Tuyau 2 : interruption, entrant (0x81), bInterval 2.
        new byte[] { 7, 5, 0x81, 0x03, 8, 0, 2 }.CopyTo(info, 46);

        FrequenceSouris.Lire(info, composite: false).Should().Be((1, 2));
    }

    // F-25 : un appareil composite (souris + clavier sur un récepteur) a plusieurs points d'accès d'interruption ; le plus
    // petit intervalle peut être celui du clavier. Plusieurs intervalles différents ⇒ non lu.
    [Fact]
    public void Un_appareil_composite_aux_intervalles_differents_n_est_pas_lu()
    {
        var info = new byte[35 + 2 * 11];
        info[23] = 1;
        BitConverter.GetBytes(2).CopyTo(info, 27);
        new byte[] { 7, 5, 0x81, 0x03, 8, 0, 1 }.CopyTo(info, 35); // clavier, 1 ms
        new byte[] { 7, 5, 0x82, 0x03, 8, 0, 8 }.CopyTo(info, 46); // souris, 8 ms

        FrequenceSouris.Lire(info, composite: true).Should().BeNull();
        FrequenceSouris.Lire(info, composite: false).Should().Be((1, 1));
    }

    // F-25 : un pavé tactile (I2C, PS/2) n'est ni USB ni Bluetooth : rien n'est dit, pas « souris non lue ».
    [Theory]
    [InlineData(@"ACPI\SYN1234\4&ABC", false)]
    [InlineData(@"HID\VEN_ELAN&DEV_0001&Col02\5&1", false)]
    [InlineData(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B019&Col01\9&1", true)]
    [InlineData(@"HID\{00001812-0000-1000-8000-00805F9B34FB}_Dev_VID&02046d_PID&b03a&Col01\a&1", true)]
    public void Seule_une_souris_Bluetooth_se_dit_non_lue_hors_USB(string id, bool bluetooth)
        => FrequenceSouris.EstBluetooth(id).Should().Be(bluetooth);

    [Fact]
    public void Une_souris_a_125_Hz_ou_moins_est_signalee()
    {
        var c = FrequenceSouris.Juger("Souris X", 125);

        c.Nom.Should().Be("fréquence de la souris · Souris X");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Au_dessus_c_est_un_maximum_annonce_sans_geste()
    {
        var c = FrequenceSouris.Juger("Souris USB X", 1000);

        c.ValeurCourante.Should().Be("jusqu'à 1000 Hz");
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("maximum");
    }

    [Fact]
    public void Une_souris_non_lue_le_dit()
        => FrequenceSouris.Juger("Souris Bluetooth", null).Statut.Should().Be(RepairStatus.Unknown);
}
