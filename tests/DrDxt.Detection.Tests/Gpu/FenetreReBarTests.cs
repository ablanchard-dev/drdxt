using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Gpu;

// Le Resizable BAR prouvé par la plage allouée, comparée à la mémoire vidéo de la même carte.
public sealed class FenetreReBarTests
{
    // Les cartes non listées par WMI : un constat inconnu, pas une famille muette.
    [Fact]
    public void Des_cartes_non_listees_se_disent_non_lues()
    {
        var wmi = Substitute.For<DrDxt.Detection.Wmi.IWmiQuery>();
        wmi.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>.Fail(ErrorCodes.WmiFail, "non"));

        FenetreReBar.LireEtJuger(wmi).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
    }

    // Relevé sur la machine de référence : RTX 4060, 8192 Mio à 0x4000000000, 8 585 740 288 octets de VRAM.
    [Fact]
    public void La_plage_de_la_machine_de_reference_prouve_ReBAR_et_Above_4G()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = FenetreReBar.Juger(new("NVIDIA GeForce RTX 4060", 8192UL << 20, 0x4000000000, 8_585_740_288));
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.Explication.Should().Contain("8192 Mio pour 8188 Mio").And.Contain("Above 4G Decoding est appliqué");
    }

    // Notre règle : une plage au-dessus de 4 Gio ne suffit pas à dire « actif » ; seule la comparaison à la mémoire vidéo.
    [Fact]
    public void Une_plage_au_dessus_de_4_Gio_mais_petite_n_est_pas_dite_active()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = FenetreReBar.Juger(new("carte", 2048UL << 20, 0x4000000000, 16UL << 30));
        c.Statut.Should().NotBe(RepairStatus.ExpectedDefault);
        c.Explication.Should().Contain("partiel");
    }

    [Fact]
    public void L_ouverture_classique_dit_inactif_et_sans_memoire_video_rien_n_est_tranche()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        FenetreReBar.Juger(new("carte", 256UL << 20, 0xE0000000, 8UL << 30)).Explication.Should().Contain("n'est pas appliqué");
        FenetreReBar.Juger(new("carte", 8192UL << 20, 0x4000000000, null)).Statut.Should().Be(RepairStatus.Unknown);
        FenetreReBar.Juger(new("carte", null, 0, 8UL << 30)).Statut.Should().Be(RepairStatus.Unknown);
    }

    // Une puce graphique intégrée déclare une petite mémoire « dédiée » (128 Mio) prise sur la mémoire du système, et son
    // ouverture fait 256 Mio ou plus : comparer les deux dirait « actif » sans rien prouver. Sous l'ouverture classique,
    // la mémoire déclarée ne tranche pas : non lu.
    [Fact]
    public void Une_memoire_dediee_sous_l_ouverture_classique_ne_dit_jamais_actif()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = FenetreReBar.Juger(new("Intel(R) UHD Graphics 770", 256UL << 20, 0x4000000000, 128UL << 20));
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("ne tranche pas");
    }

    [Fact]
    public void Chaque_carte_porte_son_nom_dans_le_constat()
        => FenetreReBar.Juger(new("Intel Arc A770", 16UL << 30, 0x4000000000, 16UL << 30)).Nom.Should().Contain("Intel Arc A770");
}
