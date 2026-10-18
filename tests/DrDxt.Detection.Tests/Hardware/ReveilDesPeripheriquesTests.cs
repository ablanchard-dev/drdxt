using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Le temps de réveil annoncé par les pilotes, en unités de 100 µs ; une valeur incohérente est dite incohérente.
public sealed class ReveilDesPeripheriquesTests
{
    [Fact]
    public void La_latence_se_lit_en_unites_de_100_microsecondes()
        => ReveilDesPeripheriques.PireReveilMs(new("webcam", 0x0F, 0, 0, 20_000)).Should().Be(2000);

    // Notre règle : on ne devine pas l'unité. Une latence donnée pour un état non pris en charge est incohérente.
    [Fact]
    public void Une_latence_pour_un_etat_non_pris_en_charge_est_dite_incoherente_pas_convertie()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        double.IsNaN(ReveilDesPeripheriques.PireReveilMs(new("x", 0x09, 5000, 0, 0))!.Value).Should().BeTrue();
        ReveilDesPeripheriques.Juger([("x", new("x", 0x09, 5000, 0, 0))]).Explication.Should().Contain("Valeur incohérente pour x");
    }

    // Relevé sur la machine de référence : D3 pris en charge (0x7F, 0x19), latences à 0.
    [Fact]
    public void Des_latences_a_zero_ne_sont_pas_annoncees_et_rien_n_est_jamais_regle()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = ReveilDesPeripheriques.Juger([("clavier", new("clavier", 0x7F, 0, 0, 0)), ("micro", new("micro", 0x19, 0, 0, 0))]);
        c.ValeurCourante.Should().Be("non annoncé");
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("ne touche jamais à la mise en veille");
    }

    [Fact]
    public void Un_tampon_trop_court_ne_se_decode_pas()
    {
        ReveilDesPeripheriques.Decoder("x", new byte[8]).Should().BeNull();
        var b = new byte[56];
        BitConverter.GetBytes(56u).CopyTo(b, 0);
        BitConverter.GetBytes(0x0Fu).CopyTo(b, 8);
        BitConverter.GetBytes(300u).CopyTo(b, 20);
        ReveilDesPeripheriques.Decoder("x", b)!.D3.Should().Be(300u);
    }
}
