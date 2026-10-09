using DrDxt.Core;
using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// 06/10/2026 (chantier B2) : l'écran annonce dans son EDID les fréquences qu'il tient à sa résolution ; quand
// Windows en propose moins, quelque chose entre la carte et l'écran bride (câble, port, pilote). EDID relevés le 06/10 sur
// la machine de référence, numéros de série effacés.
public sealed class EcranBrideTests
{
    // Samsung 144 Hz, branché en HDMI.
    private static readonly byte[] Samsung = Convert.FromHexString("00ffffffffffff004c2dd97100000000121f010380351e782acb75a656539d27125054bfef80714f810081c081809500a9c0b3000101b37d806a703817402c2035000f282100001e000000fd0032901e9f22000a202020202020000000fc004c53323441473332780a202020000000ff0030303030303030303030303030010602031cf147903f1f04130312230907078301000067030c001000b8440474801871382d40582c45000f282100001e023a801871382d40582c45000f282100001e000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000bf");
    // ASUS 200 Hz, branché en DisplayPort.
    private static readonly byte[] Asus = Convert.FromHexString("00ffffffffffff0006b313240000000013240104b5351e783bdf3ba454519a230d4e53254a008140818081c0a9c0d1c0010101010101023a801871382d40582c4500132b2100001e000000fd0030c8f0f032010a202020202020000000fc0056473234395135520a20202020000000ff00303030303030303030303030300147020334f14b90010203041112131f3f4023090707830100006d1a0000020130c80000534d5348e6060701535325e305e321e200ea866f80a07038404030203500132b2100001a5a8780a070384d4030203500132b2100001a60b480a070381e4030404500132b2100001a00000000000000000000000000000000000000000054");

    [Fact]
    public void La_frequence_maximale_se_lit_dans_les_timings_de_l_EDID_a_la_resolution_courante()
    {
        EdidFrequences.MaxA(Asus, 1920, 1080).Should().Be(200);
        EdidFrequences.MaxA(Samsung, 1920, 1080).Should().Be(144);
    }

    // Une résolution étirée (1728×1080, relevée le 06/10 sur l'écran principal) n'est pas détaillée : le câble transporte la
    // résolution native, c'est elle qu'on compare.
    [Fact]
    public void La_resolution_native_est_celle_du_premier_timing_detaille()
    {
        EdidFrequences.Natif(Asus).Should().Be((1920, 1080));
        EdidFrequences.Natif(new byte[] { 0, 1 }).Should().BeNull();
    }

    [Fact]
    public void Une_resolution_que_l_EDID_ne_detaille_pas_n_est_pas_lue()
        => EdidFrequences.MaxA(Asus, 2560, 1440).Should().BeNull();

    [Fact]
    public void Un_EDID_illisible_n_est_pas_lu()
        => EdidFrequences.MaxA(new byte[] { 0, 1, 2 }, 1920, 1080).Should().BeNull();

    [Fact]
    public void Windows_au_niveau_de_l_ecran_rien_n_est_dit()
        => EcranBride.Juger(200, 200, 10).Should().BeNull();

    [Fact]
    public void Windows_sous_ce_que_l_ecran_annonce_c_est_dit_avec_le_branchement()
    {
        var c = EcranBride.Juger(144, 200, 5, "1920×1080")!;

        c.Nom.Should().Be("fréquence annoncée par l'écran");
        c.ValeurCourante.Should().Be("200 Hz annoncés, 144 Hz proposés");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("HDMI").And.Contain("en 1920×1080");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Sans_lecture_de_l_EDID_ou_du_maximum_de_Windows_rien_n_est_dit()
    {
        EcranBride.Juger(144, null, 5).Should().BeNull();
        EcranBride.Juger(0, 200, 5).Should().BeNull();
    }

    // Le pont vers WmiMonitorConnectionParams, dont l'instance commence par « DISPLAY\pnp\instance ».
    [Fact]
    public void Le_nom_d_interface_donne_l_instance_WMI_du_moniteur()
    {
        EdidDuMoniteur.Instance(@"\\?\DISPLAY#AUS2413#5&3ac6cfcf&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")
            .Should().Be(@"DISPLAY\AUS2413\5&3ac6cfcf&0&UID4354");
        EdidDuMoniteur.Instance("pas un nom d'interface").Should().BeNull();
    }

    // Revue F-17 : la tolérance d'arrondi est un bord, il se teste.
    [Fact]
    public void Un_hertz_d_ecart_est_un_arrondi_deux_sont_une_bride()
    {
        EcranBride.Juger(199, 200, 10).Should().BeNull();
        EcranBride.Juger(198, 200, 10).Should().NotBeNull();
    }

    // F-16 : la cause la plus fréquente, et la seule gratuite : le menu de l'écran.
    [Fact]
    public void Le_menu_de_l_ecran_est_nomme_avant_le_cable()
        => EcranBride.Juger(144, 200, 10)!.Explication.Should().Contain("menu de l'écran");

    // F-14 : une dalle de portable (DisplayPort embarqué, ou sortie interne) n'a ni câble ni port à changer.
    [Theory]
    [InlineData(11u)]
    [InlineData(0x8000000Bu)]
    [InlineData(0x80000000u)]
    public void Une_dalle_interne_n_envoie_pas_changer_de_cable(uint sortie)
        => EcranBride.Juger(60, 165, sortie)!.Explication.Should().NotContain("câble").And.Contain("dalle");

    // F-15 : en écrans dupliqués, Windows ne propose que les modes communs : ce n'est pas une bride.
    [Fact]
    public void Des_ecrans_dupliques_ne_sont_pas_juges()
        => EcranBride.Comparer(Asus, (_, _) => 144, 10, duplique: true).Should().BeNull();

    // F-18 : un EDID illisible est un « non lu » compté, pas un silence.
    [Fact]
    public void Un_EDID_illisible_se_dit_non_lu()
    {
        EcranBride.Comparer(null, (_, _) => 144, 10, duplique: false)!.Statut.Should().Be(RepairStatus.Unknown);
        EcranBride.Comparer(new byte[] { 1, 2 }, (_, _) => 144, 10, duplique: false)!.Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void La_comparaison_se_fait_a_la_resolution_native()
    {
        EcranBride.Comparer(Asus, (l, h) => l == 1920 && h == 1080 ? 144 : 0, 10, duplique: false)!
            .ValeurCourante.Should().Be("200 Hz annoncés, 144 Hz proposés");
        EcranBride.Comparer(Asus, (_, _) => 200, 10, duplique: false).Should().BeNull();
    }

    [Fact]
    public void Un_branchement_inconnu_ne_se_nomme_pas()
        => EcranBride.Juger(144, 200, null)!.Explication.Should().NotContain("branché en");
}
