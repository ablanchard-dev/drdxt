using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Les couches logicielles d'entrée : dites par l'arbre des périphériques et l'éditeur des filtres, jamais par le fabricant.
public sealed class CouchesDEntreeTests
{
    // Chaîne relevée sur la machine de référence (souris USB derrière un concentrateur).
    private static readonly string[] SourisPhysique =
    [
        @"HID\VID_0738&PID_1703\7&D18AFE7&1&0000", @"USB\VID_0738&PID_1703\6&34110B8C&0&1", @"USB\VID_05E3&PID_0608\5&232B4DD6&0&5",
        @"USB\ROOT_HUB30\4&10EA92FE&0&0", @"PCI\VEN_8086&DEV_7A60&SUBSYS_7D981462&REV_11\3&11583659&0&A0", @"ACPI\PNP0A08\0",
        @"ACPI_HAL\PNP0C08\0", @"ROOT\ACPI_HAL\0000", @"HTREE\ROOT\0",
    ];

    [Fact]
    public void Un_appareil_rattache_a_un_controleur_PCI_n_est_pas_virtuel()
        => CouchesDEntree.Virtuel(SourisPhysique).Should().BeFalse();

    // Notre règle : le fabricant déclaré ne compte pas. Une manette au VID de Microsoft branchée en USB est physique, la
    // même identité posée sous un bus énuméré à la racine est virtuelle.
    [Fact]
    public void L_arbre_decide_et_jamais_le_VID()
    {
        CouchesDEntree.Virtuel([@"HID\VID_045E&PID_028E\1", @"USB\VID_045E&PID_028E\2", @"USB\ROOT_HUB30\3", @"PCI\VEN_8086&DEV_7A60\4", @"ROOT\ACPI_HAL\0000"])
            .Should().BeFalse();
        CouchesDEntree.Virtuel([@"HID\VID_045E&PID_028E\1", @"USB\VID_045E&PID_028E\2", @"ROOT\SYSTEM\0001", @"HTREE\ROOT\0"])
            .Should().BeTrue();
    }

    // Notre règle : le pilote du bus (radio Bluetooth, concentrateur) n'est pas une couche de l'appareil qui passe par lui.
    // Chaîne relevée sur la machine de référence : une manette Bluetooth derrière la radio USB 8087:0033.
    [Fact]
    public void Les_filtres_du_bus_ne_sont_jamais_lus_comme_une_couche_de_l_appareil()
    {
        string[] manetteBluetooth =
        [
            @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0DF2\7&3B0CC7A8&0&E8473AA3B4A1_C00000000",
            @"BTH\MS_BTHBRB\6&1", @"USB\VID_8087&PID_0033\5&232B4DD6&0&14", @"USB\ROOT_HUB30\4&10EA92FE&0&0", @"PCI\VEN_8086&DEV_7A60\3",
        ];
        CouchesDEntree.NoeudsDeLAppareil(manetteBluetooth, _ => null).Should().ContainSingle().Which.Should().StartWith("BTHENUM");
        CouchesDEntree.NoeudsDeLAppareil(SourisPhysique, n => n.StartsWith(@"USB\VID_05E3") ? "USBHUB3" : null)
            .Should().Equal(SourisPhysique[0], SourisPhysique[1]);
    }

    [Fact]
    public void Les_filtres_de_Microsoft_ne_sont_pas_une_couche_tierce()
    {
        var kbdclass = new CouchesDEntree.Filtre("kbdclass", "Microsoft Corporation", "10.0.26100.1", "Keyboard");
        CouchesDEntree.CoucheActive([new("souris", false, [])], [kbdclass]).Should().BeFalse();
        CouchesDEntree.Juger([new("souris", false, [])], [kbdclass]).Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Un_filtre_tiers_sur_un_appareil_est_dit_pilote_noyau_tiers_et_marque_la_mesure()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var filtre = new CouchesDEntree.Filtre("filtreusb", "Éditeur inconnu", "2.0", @"USB\VID_054C&PID_0CE6");
        var appareils = new[] { new CouchesDEntree.Appareil("manette", false, [filtre]) };
        CouchesDEntree.CoucheActive(appareils, []).Should().BeTrue();
        CouchesDEntree.Juger(appareils, []).Explication.Should().Contain("Pilote noyau filtre tiers filtreusb").And.Contain("à travers cette couche");
    }

    [Fact]
    public void Un_editeur_illisible_compte_comme_tiers_et_une_manette_virtuelle_est_nommee()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        CouchesDEntree.Tiers(new("x", null, null, "HIDClass")).Should().BeTrue();
        CouchesDEntree.Juger([new("Controller (XBOX 360 For Windows)", true, [])], []).Explication.Should().Contain("ce n'est pas ta manette");
        CouchesDEntree.Juger(null, []).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\kbdclass.sys", @"System32\drivers\kbdclass.sys")]
    [InlineData(@"System32\drivers\mouclass.sys", @"System32\drivers\mouclass.sys")]
    public void Un_chemin_de_pilote_se_ramene_au_dossier_de_Windows(string image, string fin)
        => CouchesDEntree.CheminNoyau(image, "x").Should().EndWith(fin);

    // Un service filtre sans ImagePath tourne depuis son fichier par défaut : il se lit là, au lieu de passer pour un
    // filtre tiers faute d'éditeur lu (un faux « couche active » qui ferait douter de toute mesure d'entrée).
    [Fact]
    public void Un_service_sans_ImagePath_se_lit_dans_son_fichier_par_defaut()
        => CouchesDEntree.FichierDuService("kbdclass", null).Should()
            .Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "kbdclass.sys"));

    // Microsoft Learn, SwDeviceCreate : le parent d'un appareil logiciel (SWD\) peut être la racine (HTREE\ROOT\0) ou « un vrai
    // appareil dont il étend la fonction ». Sans parent physique dans son arbre, il est virtuel ; avec, il ne l'est pas.
    [Fact]
    public void Un_appareil_logiciel_n_est_virtuel_que_sans_parent_physique()
    {
        CouchesDEntree.Virtuel([@"HID\SWD_PAD\1", @"SWD\PADLOGICIEL\1", @"HTREE\ROOT\0"]).Should().BeTrue();
        CouchesDEntree.Virtuel([@"HID\SWD_PAD\1", @"SWD\PADLOGICIEL\1", @"USB\VID_045E&PID_028E\2", @"USB\ROOT_HUB30\3",
            @"PCI\VEN_8086&DEV_7A60\4", @"HTREE\ROOT\0"]).Should().BeFalse();
        CouchesDEntree.Virtuel([@"HID\SWD_PAD\1", @"SWD\PADLOGICIEL\1", @"BTHENUM\{X}\2", @"HTREE\ROOT\0"]).Should().BeFalse();
    }
}
