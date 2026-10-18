using System.Globalization;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le guide BIOS se lit au téléphone pendant que le PC est dans le BIOS ; le QR porte
// le texte entier, sans réseau.
public sealed class GuideBiosAuTelephoneTests
{
    private static string Fr(string cle) => new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new CultureInfo("fr"));

    [Fact]
    public void Le_qr_porte_la_carte_les_touches_chaque_etape_et_le_secours()
    {
        var g = new BiosGuideViewModel(ScanViewModelTestSeams.Guide(steps: ScanViewModelTestSeams.Step()), Fr);

        var lignes = g.TexteTelephone.Split('\n');
        lignes[0].Should().Contain("MSI MAG B650 TOMAHAWK WIFI");
        lignes.Should().Contain(g.EnterInstruction).And.Contain(g.SaveInstruction);
        lignes.Should().Contain(l => l.StartsWith("1. ") && l.EndsWith("Ai Tweaker  →  EXPO  →  = EXPO I") && !l.Contains(": :"));
        lignes.Should().Contain(Fr("Scan.Bios.Guide.Qr.Secours"));
        g.QrTelephone.Should().NotBeNull();
        g.QrTelephone!.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        // Relu hors CI par un vrai décodeur (OpenCV) : DRDXT_QR_SORTIE=chemin.png écrit l'image et son texte attendu.
        if (Environment.GetEnvironmentVariable("DRDXT_QR_SORTIE") is { Length: > 0 } sortie)
        {
            System.IO.File.WriteAllBytes(sortie, g.QrTelephone);
            System.IO.File.WriteAllText(sortie + ".txt", g.TexteTelephone);
        }
    }

    // Une carte jamais vue renvoie au manuel de son constructeur ; un lien seulement là où l'adresse est
    // prouvée (MSI, page relevée le 07/10 pour la carte de la machine de référence).
    [Fact]
    public void Le_lien_du_manuel_n_existe_que_pour_un_motif_prouve()
    {
        BiosGuideViewModel.LienDuManuel(BiosVendor.Msi, "PRO B760-P WIFI DDR4 (MS-7D98)")
            .Should().Be("https://www.msi.com/Motherboard/PRO-B760-P-WIFI-DDR4/support");
        BiosGuideViewModel.LienDuManuel(BiosVendor.Asus, "ROG STRIX B650-A GAMING WIFI").Should().BeNull();
        BiosGuideViewModel.LienDuManuel(BiosVendor.Msi, "PRO B760/../x").Should().BeNull();
        BiosGuideViewModel.LienDuManuel(BiosVendor.Msi, "").Should().BeNull();
        // Une carte qui ne dit que son code interne n'a pas de page à ce nom.
        BiosGuideViewModel.LienDuManuel(BiosVendor.Msi, "MS-7C02").Should().BeNull();
    }

    [Fact]
    public void Une_carte_non_verifiee_renvoie_au_manuel_et_une_verifiee_non()
    {
        var guide = ScanViewModelTestSeams.Guide(GuideConfidence.UnverifiedFallback, steps: ScanViewModelTestSeams.Step())
            with { Vendor = BiosVendor.Msi, BoardProduct = "PRO B760-P WIFI DDR4 (MS-7D98)" };
        var g = new BiosGuideViewModel(guide, Fr);

        g.ManuelDeLaCarte.Should().Contain("PRO B760-P WIFI DDR4");
        g.LienManuelVisible.Should().BeTrue();
        g.TexteTelephone.Should().Contain("https://www.msi.com/Motherboard/PRO-B760-P-WIFI-DDR4/support");
        new BiosGuideViewModel(ScanViewModelTestSeams.Guide(steps: ScanViewModelTestSeams.Step()), Fr).ManuelDeLaCarte.Should().BeNull();
    }

    // Un guide réel (carte non vérifiée, quatre réglages, lien du manuel) tient dans un QR lisible : au
    // plus la version 25, affiché à sa taille, 4 pixels par module. Relu hors CI par OpenCV à cette taille même
    // (DRDXT_QR_REEL=chemin.png).
    [Fact]
    public void Un_guide_reel_tient_dans_un_qr_lisible_a_la_taille_ou_il_est_montre()
    {
        var etapes = new[]
        {
            ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp", "OC  →  Extreme Memory Profile (XMP)  →  = Profile 1", 1),
            ScanViewModelTestSeams.Step(FirmwareSettingKind.Above4G, "Bios.Guide.Step.Above4G", "Settings  →  Advanced  →  PCI Subsystem Settings  →  Above 4G memory/Crypto Currency mining  →  = Enabled", 2),
            ScanViewModelTestSeams.Step(FirmwareSettingKind.Csm, "Bios.Guide.Step.Csm", "Settings  →  Advanced  →  Windows OS Configuration  →  BIOS UEFI/CSM Mode  →  = UEFI", 3),
            ScanViewModelTestSeams.Step(FirmwareSettingKind.ReBar, "Bios.Guide.Step.ReBar", "Settings  →  Advanced  →  PCI Subsystem Settings  →  Re-Size BAR Support  →  = Enabled", 4),
        };
        var guide = ScanViewModelTestSeams.Guide(GuideConfidence.UnverifiedFallback, board: "Micro-Star International Co., Ltd. PRO B760-P WIFI DDR4 (MS-7D98)", steps: etapes)
            with { BoardProduct = "PRO B760-P WIFI DDR4 (MS-7D98)" };
        var g = new BiosGuideViewModel(guide, Fr);

        g.QrVisible.Should().BeTrue();
        g.ModulesQr.Should().BeLessThanOrEqualTo(BiosGuideViewModel.ModulesMaximum);
        g.TexteTelephone.Should().Contain("https://www.msi.com/Motherboard/PRO-B760-P-WIFI-DDR4/support");
        if (Environment.GetEnvironmentVariable("DRDXT_QR_REEL") is { Length: > 0 } sortie)
        {
            System.IO.File.WriteAllBytes(sortie, g.QrTelephone!);
            System.IO.File.WriteAllText(sortie + ".txt", g.TexteTelephone);
        }
    }

    // La carte ne dit plus « tu n'as besoin de rien d'autre » à côté de « le manuel fait foi ».
    [Fact]
    public void Une_carte_non_verifiee_ne_dit_pas_deux_choses_contraires()
        => new BiosGuideViewModel(ScanViewModelTestSeams.Guide(GuideConfidence.UnverifiedFallback, steps: ScanViewModelTestSeams.Step()), Fr)
            .ConfidenceLabel.Should().NotContain("rien d'autre");

    // Des valeurs toutes différentes : la compression de l'adresse ne les réduit pas, et ni la page ni le texte ne tiennent
    // dans la version 25.
    [Fact]
    public void Un_guide_trop_long_pour_un_qr_garde_son_texte_sans_image()
    {
        var etapes = Enumerable.Range(1, 400)
            .Select(i => ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp", Guid.NewGuid().ToString(), i)).ToArray();
        var g = new BiosGuideViewModel(ScanViewModelTestSeams.Guide(steps: etapes), Fr);

        g.QrVisible.Should().BeFalse();
        g.ModulesQr.Should().Be(0);
        g.Steps.Should().HaveCount(400);
    }
}
