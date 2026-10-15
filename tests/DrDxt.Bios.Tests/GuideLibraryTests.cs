using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Tests de la bibliothèque de guides pilotée par les données. La précision est le produit :
/// une carte mère couverte → gabarit vérifié exact ; une carte non couverte → un repli étiqueté
/// « non vérifié » qui porte toujours le nom exact du réglage (le joueur n'a jamais besoin
/// d'un tiers). Les données sont validées défensivement (corrompues → Fail propre, jamais un plantage).
/// </summary>
public class GuideLibraryTests
{
    private static IGuideLibrary Lib() => GuideLibraryFactory.CreateDefault();

    private static GuideTemplate Resolve(BiosVendor vendor, string gen)
    {
        var r = Lib().Resolve(vendor, gen);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    // ── La bibliothèque embarquée se charge et se valide : chaque gabarit livré est valide. ──

    [Fact]
    public void Default_library_loads_clean()
    {
        var r = Lib().Resolve(BiosVendor.Asus, "Generic");
        r.IsOk.Should().BeTrue(r.Error?.Message);
    }

    // ── Résolution exacte : un constructeur couvert × une génération d'interface → meilleur repli
    //    connu, non vérifié (seule une entrée carte + BIOS de la base peut être vérifiée). ──

    [Fact]
    public void Resolves_covered_msi_click_bios_exactly_but_not_verified()
    {
        var t = Resolve(BiosVendor.Msi, "Click BIOS 5");

        t.Vendor.Should().Be(BiosVendor.Msi);
        t.IsVerified.Should().BeFalse("a vendor × UI-generation match is best-known, not board-confirmed");
        t.EnterKey.Should().NotBeNullOrWhiteSpace();
        t.SaveKey.Should().NotBeNullOrWhiteSpace();

        // Le chemin XMP/EXPO est présent avec un chemin de menu exact et un nom de réglage exact.
        var xmp = t.Paths.Single(p => p.Setting == FirmwareSettingKind.XmpExpo);
        xmp.MenuPath.Should().NotBeNullOrWhiteSpace();
        xmp.SettingName.Should().NotBeNullOrWhiteSpace();
        xmp.TargetValue.Should().NotBeNullOrWhiteSpace();

        // Les réglages de la chaîne ReBAR sont tous navigables.
        t.Paths.Select(p => p.Setting).Should().Contain(new[]
        {
            FirmwareSettingKind.XmpExpo, FirmwareSettingKind.Above4G,
            FirmwareSettingKind.Csm, FirmwareSettingKind.ReBar, FirmwareSettingKind.X3dCppc,
        });
    }

    [Theory]
    [InlineData(BiosVendor.Asus, "AI Tweaker UEFI")]
    [InlineData(BiosVendor.Msi, "Click BIOS 5")]
    [InlineData(BiosVendor.Gigabyte, "Aorus UEFI")]
    [InlineData(BiosVendor.Asrock, "ASRock UEFI")]
    public void Each_vendor_recent_entry_is_best_known_not_verified(BiosVendor vendor, string gen)
    {
        // La génération « récente » choisie par constructeur se résout et est complète, mais c'est
        // un meilleur repli connu, jamais « vérifié pour votre carte exacte ».
        var t = Resolve(vendor, gen);
        t.Vendor.Should().Be(vendor);
        t.IsVerified.Should().BeFalse("no board+BIOS KB key → cannot be verified");
        t.Paths.Should().NotBeEmpty();

        // Et une génération inconnue ne laisse jamais le joueur sans rien.
        Lib().Resolve(vendor, "__definitely_unknown_generation__").IsOk.Should().BeTrue();
    }

    // ── La couture : une vraie entrée carte + version de BIOS de la base est rendue vérifiée quand
    //    la carte et la version détectées correspondent, et la même bibliothèque retombe sur le non
    //    vérifié sinon. Preuve que « vérifié » ne se gagne que sur une correspondance carte + version. ──

    [Fact]
    public void Board_and_bios_version_kb_entry_earns_verified_only_on_an_exact_match()
    {
        var boardEntry = new GuideTemplate(
            Vendor: BiosVendor.Asus,
            BiosUiGeneration: "AI Tweaker UEFI",
            IsVerified: true,
            Board: "ROG STRIX B650-A GAMING WIFI",
            BiosVersion: "2.0",
            EnterKey: "Del", SaveKey: "F10",
            Paths: new[]
            {
                new GuideSettingPath(FirmwareSettingKind.XmpExpo,
                    "Ai Tweaker > Ai Overclock Tuner", "Ai Overclock Tuner", "EXPO I"),
            });
        var genericFallback = new GuideTemplate(
            Vendor: BiosVendor.Unknown, BiosUiGeneration: "Generic", IsVerified: false,
            Board: "", BiosVersion: "", EnterKey: "Del", SaveKey: "F10",
            Paths: new[]
            {
                new GuideSettingPath(FirmwareSettingKind.XmpExpo, "", "XMP / EXPO", "Enabled"),
            });
        IGuideLibrary lib = new GuideLibrary(new[] { boardEntry, genericFallback });

        // Carte + version exactes → l'entrée vérifiée de la base.
        var hit = lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI",
            "ROG STRIX B650-A GAMING WIFI", "2.0");
        hit.IsOk.Should().BeTrue(hit.Error?.Message);
        hit.Value!.IsVerified.Should().BeTrue();
        hit.Value.Board.Should().Be("ROG STRIX B650-A GAMING WIFI");

        // Même carte, autre version de BIOS → pas l'entrée vérifiée → repli non vérifié.
        lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI",
                "ROG STRIX B650-A GAMING WIFI", "9.9")
            .Value!.IsVerified.Should().BeFalse();

        // Aucune clé de carte fournie → repli non vérifié.
        lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI").Value!.IsVerified.Should().BeFalse();
    }

    // ── Repli : constructeur couvert, génération non couverte → repli étiqueté. ──

    [Fact]
    public void Uncovered_generation_returns_vendor_fallback_unverified()
    {
        var t = Resolve(BiosVendor.Asus, "some-future-ui-2099");

        t.Vendor.Should().Be(BiosVendor.Asus);
        t.IsVerified.Should().BeFalse("an uncovered board must be labelled unverified");

        // Le repli porte toujours le nom exact de chaque réglage (le joueur n'a jamais besoin
        // d'un tutoriel ni d'une IA).
        t.Paths.Should().NotBeEmpty();
        t.Paths.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.SettingName));
        t.Paths.Select(p => p.Setting).Should().Contain(FirmwareSettingKind.XmpExpo);
    }

    // ── Constructeur inconnu → repli générique le plus prudent, jamais un échec dur. ──

    [Fact]
    public void Unknown_vendor_returns_generic_fallback_unverified()
    {
        var t = Resolve(BiosVendor.Unknown, "Generic");

        t.IsVerified.Should().BeFalse();
        t.Paths.Should().NotBeEmpty();
        // Les noms exacts des réglages sont toujours présents, pour que le joueur les trouve partout.
        t.Paths.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.SettingName));
    }

    // ── Correspondance fabricant → BiosVendor (insensible à la casse et aux espaces). ──

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", BiosVendor.Asus)]
    [InlineData("ASUS", BiosVendor.Asus)]
    [InlineData("Micro-Star International Co., Ltd.", BiosVendor.Msi)]
    [InlineData("MSI", BiosVendor.Msi)]
    [InlineData("Gigabyte Technology Co., Ltd.", BiosVendor.Gigabyte)]
    [InlineData("GIGABYTE", BiosVendor.Gigabyte)]
    [InlineData("ASRock", BiosVendor.Asrock)]
    [InlineData("  asrock  ", BiosVendor.Asrock)]
    [InlineData("Some OEM Ltd", BiosVendor.Unknown)]
    [InlineData("", BiosVendor.Unknown)]
    public void Maps_manufacturer_to_vendor(string manufacturer, BiosVendor expected)
    {
        BiosVendorMapper.FromManufacturer(manufacturer).Should().Be(expected);
    }

    // ── Détection de la génération d'interface BIOS depuis le constructeur : ne lève jamais, a un défaut. ──

    [Fact]
    public void Ui_generation_detection_defaults_to_generic_when_unknown()
    {
        // Une version de BIOS vide ou bizarre doit quand même produire un jeton de génération utilisable.
        BiosUiGenerationDetector.Detect(BiosVendor.Msi, new BiosInfo("", null, true))
            .Should().NotBeNullOrWhiteSpace();
        BiosUiGenerationDetector.Detect(BiosVendor.Unknown, new BiosInfo("x", null, true))
            .Should().NotBeNullOrWhiteSpace();
    }
}
