using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Tests for the data-driven guide library. PRECISION is the product (decision §9):
/// a covered board → exact verified template; an uncovered board → a labelled
/// "unverified" fallback that always carries the exact setting NAME (the client never
/// needs a third party). Library data is validated defensively (corrupt → clean Fail,
/// never a crash).
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

    // ── The embedded library loads & validates: every shipped template is valid. ──

    [Fact]
    public void Default_library_loads_clean()
    {
        var r = Lib().Resolve(BiosVendor.Asus, "Generic");
        r.IsOk.Should().BeTrue(r.Error?.Message);
    }

    // ── Exact resolution: a covered vendor × UI generation → best-known fallback that
    //    is not verified (decision §9.1: only a board+BIOS KB hit can be verified). ──

    [Fact]
    public void Resolves_covered_msi_click_bios_exactly_but_not_verified()
    {
        var t = Resolve(BiosVendor.Msi, "Click BIOS 5");

        t.Vendor.Should().Be(BiosVendor.Msi);
        t.IsVerified.Should().BeFalse("a vendor × UI-generation match is best-known, not board-confirmed");
        t.EnterKey.Should().NotBeNullOrWhiteSpace();
        t.SaveKey.Should().NotBeNullOrWhiteSpace();

        // XMP/EXPO path is present with an exact menu path AND an exact setting name.
        var xmp = t.Paths.Single(p => p.Setting == FirmwareSettingKind.XmpExpo);
        xmp.MenuPath.Should().NotBeNullOrWhiteSpace();
        xmp.SettingName.Should().NotBeNullOrWhiteSpace();
        xmp.TargetValue.Should().NotBeNullOrWhiteSpace();

        // The ReBAR chain settings are all navigable.
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
        // The curated "recent" generation per vendor resolves and is complete, but it is
        // a best-known fallback — never "Verified for your exact board".
        var t = Resolve(vendor, gen);
        t.Vendor.Should().Be(vendor);
        t.IsVerified.Should().BeFalse("no board+BIOS KB key → cannot be verified");
        t.Paths.Should().NotBeEmpty();

        // And an unknown generation still never strands the user.
        Lib().Resolve(vendor, "__definitely_unknown_generation__").IsOk.Should().BeTrue();
    }

    // ── The SEAM: a real board + BIOS-version KB entry IS returned as Verified when the
    //    detected board+version match, and the same library falls back to unverified
    //    otherwise. This proves "Verified" is earned only on a board+version match. ──

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

        // Exact board + version → the verified KB entry.
        var hit = lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI",
            "ROG STRIX B650-A GAMING WIFI", "2.0");
        hit.IsOk.Should().BeTrue(hit.Error?.Message);
        hit.Value!.IsVerified.Should().BeTrue();
        hit.Value.Board.Should().Be("ROG STRIX B650-A GAMING WIFI");

        // Same board, DIFFERENT bios version → not the verified entry → unverified fallback.
        lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI",
                "ROG STRIX B650-A GAMING WIFI", "9.9")
            .Value!.IsVerified.Should().BeFalse();

        // No board key supplied at all → unverified fallback.
        lib.Resolve(BiosVendor.Asus, "AI Tweaker UEFI").Value!.IsVerified.Should().BeFalse();
    }

    // ── Fallback (§16): covered vendor, uncovered generation → labelled fallback. ──

    [Fact]
    public void Uncovered_generation_returns_vendor_fallback_unverified()
    {
        var t = Resolve(BiosVendor.Asus, "some-future-ui-2099");

        t.Vendor.Should().Be(BiosVendor.Asus);
        t.IsVerified.Should().BeFalse("an uncovered board must be labelled unverified");

        // The fallback still carries the exact setting name for every setting (the
        // client never needs GPT/a tutorial).
        t.Paths.Should().NotBeEmpty();
        t.Paths.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.SettingName));
        t.Paths.Select(p => p.Setting).Should().Contain(FirmwareSettingKind.XmpExpo);
    }

    // ── Unknown vendor → most-prudent generic fallback, never a hard failure. ──

    [Fact]
    public void Unknown_vendor_returns_generic_fallback_unverified()
    {
        var t = Resolve(BiosVendor.Unknown, "Generic");

        t.IsVerified.Should().BeFalse();
        t.Paths.Should().NotBeEmpty();
        // Exact setting names are still present so the user can find them anywhere.
        t.Paths.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.SettingName));
    }

    // ── Manufacturer → BiosVendor mapping (case/space-insensitive). ──

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

    // ── BIOS-UI generation detection from the vendor: never throws, has a default. ──

    [Fact]
    public void Ui_generation_detection_defaults_to_generic_when_unknown()
    {
        // An empty/odd BIOS version must still produce a usable generation token.
        BiosUiGenerationDetector.Detect(BiosVendor.Msi, new BiosInfo("", null, true))
            .Should().NotBeNullOrWhiteSpace();
        BiosUiGenerationDetector.Detect(BiosVendor.Unknown, new BiosInfo("x", null, true))
            .Should().NotBeNullOrWhiteSpace();
    }
}
