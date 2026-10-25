using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les registres d'un Ryzen, décodés par le cœur : température (k10temp), adresses SMN permises, cause des redémarrages
// (S5_RESET_STATUS). Pur : aucune lecture de machine, des valeurs de registre construites bit à bit.
public sealed class RegistresAmdTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

    // 50 °C : 400 pas de 0,125 °C dans les bits 31:21.
    [Fact]
    public void Tctl_se_lit_dans_les_bits_hauts_par_pas_de_un_huitieme()
        => TemperatureAmd.Tctl(400u << 21).Should().Be(50);

    [Theory]
    [InlineData(1u << 19)]
    [InlineData(0x30000u)]
    public void La_plage_etendue_decale_Tctl_de_49_degres(uint drapeau)
        => TemperatureAmd.Tctl((400u << 21) | drapeau).Should().Be(1);

    [Fact]
    public void Un_ccd_se_lit_seulement_quand_son_bit_de_validite_est_leve()
    {
        TemperatureAmd.TemperatureCcd(0x800u | 792).Should().Be(50);
        TemperatureAmd.TemperatureCcd(792).Should().BeNull("CCD absent : le bit 11 est baissé");
    }

    // Tous les bits à 1 : la réponse d'erreur du SMN, pas 206,9 °C ni un CCD présent.
    [Fact]
    public void Une_reponse_d_erreur_n_est_pas_une_temperature()
    {
        TemperatureAmd.Tctl(uint.MaxValue).Should().BeNull();
        TemperatureAmd.TemperatureCcd(uint.MaxValue).Should().BeNull();
    }

    // Un registre qui n'est pas tout à 1 peut encore dire une température hors du physique (0xFFFFFFFE se lit 206,9 °C) : elle
    // n'est pas une mesure. Une vraie température haute reste lue.
    [Fact]
    public void Une_temperature_hors_du_physique_n_est_pas_une_mesure()
    {
        TemperatureAmd.Tctl(uint.MaxValue - 1).Should().BeNull();
        TemperatureAmd.TemperatureCcd(0x800u | 0x7FE).Should().BeNull();
        TemperatureAmd.Tctl(880u << 21).Should().Be(110, "témoin : 110 °C est une vraie température");
        TemperatureAmd.TemperatureCcd(0x800u | 1272).Should().Be(110);
    }

    [Fact]
    public void Les_ccd_d_un_zen4_bureau_sont_a_leur_decalage()
    {
        TemperatureAmd.Ccd(0x19, 0x61).Should().Be((0x308u, 8));
        TemperatureAmd.Ccd(0x06, 0x00).Should().BeNull("hors des familles que k10temp connaît");
        TemperatureAmd.AdresseCcd(0x308, 1).Should().Be(0x59800u + 0x308 + 4);
    }

    [Fact]
    public void Le_decalage_tdie_ne_touche_que_les_modeles_nommes()
    {
        TemperatureAmd.DecalageTdie(0x17, "AMD Ryzen 7 1800X Eight-Core Processor").Should().Be(20);
        TemperatureAmd.DecalageTdie(0x19, "AMD Ryzen 7 7800X3D 8-Core Processor").Should().Be(0);
        TemperatureAmd.DecalageTdie(0x17, null).Should().Be(0);
    }

    // Le module ne borne pas les adresses : la liste de DrDXT, si. Rien d'autre que la température et les timings.
    [Fact]
    public void Seules_les_adresses_de_temperature_et_de_timings_sont_permises()
    {
        SmnAmd.Autorisee(TemperatureAmd.AdresseTctl).Should().BeTrue();
        SmnAmd.Autorisee(TemperatureAmd.AdresseCcd(0x308, 7)).Should().BeTrue();
        SmnAmd.Autorisee(TimingsMemoireAmd.Adresse(0, TimingsMemoireAmd.Registres[0])).Should().BeTrue();

        SmnAmd.Autorisee(0).Should().BeFalse();
        SmnAmd.Autorisee(0x03B10524).Should().BeFalse("la boîte aux lettres du SMU n'est jamais lue par SMN");
        SmnAmd.Autorisee(TemperatureAmd.AdresseTctl + 4).Should().BeFalse();
    }

    [Fact]
    public void Sans_processeur_amd_reconnu_rien()
        => ResetAmd.Juger(null, 0x2).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData(uint.MaxValue)]
    public void Non_lu_ou_tout_a_un_se_dit_non_lu(uint? valeur)
        => ResetAmd.Juger(Zen4, valeur).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Aucun_bit_leve_se_dit_aucune_cause()
    {
        var c = ResetAmd.Juger(Zen4, 0).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Be(Docteur.T("S5.AucuneExplication"));
    }

    // Le bouton tenu 4 s est un geste ; la surchauffe (THERMTRIP) une panne. Un bit réservé ne dit rien.
    [Fact]
    public void Un_geste_s_observe_une_panne_se_signale()
    {
        ResetAmd.Juger(Zen4, 1u << 1).Single().Statut.Should().Be(RepairStatus.Observed);
        ResetAmd.Juger(Zen4, 1u << 0).Single().Statut.Should().Be(RepairStatus.Suspicious);
        ResetAmd.Bits((1u << 3) | (1u << 1)).Should().Equal(1);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Chaque_bit_connu_se_dit_dans_les_deux_langues(string langue)
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo(langue));
        foreach (var b in ResetAmd.BitsConnus) Docteur.T("S5.Bit" + b).Should().NotStartWith("S5.");
        ResetAmd.BitsDePanne.Should().BeSubsetOf(ResetAmd.BitsConnus);
    }
}
