using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Defensive validation of the guide library data (§9.6: validate all external data
/// before use). A valid payload parses; a corrupt one (missing field, bad enum,
/// empty setting name, HTML/template injection in a value) fails cleanly with
/// <see cref="ErrorCodes.BiosTemplateInvalid"/> — never an exception.
/// </summary>
public class GuideTemplateValidatorTests
{
    private static Result<IReadOnlyList<GuideTemplate>> Parse(string json)
        => GuideTemplateValidator.ParseAndValidate(json);

    private const string ValidJson = """
    [
      {
        "vendor": "Msi",
        "biosUiGeneration": "Click BIOS 5",
        "isVerified": false,
        "enterKey": "Del",
        "saveKey": "F10",
        "paths": [
          { "setting": "XmpExpo", "menuPath": "OC", "settingName": "EXPO / A-XMP", "targetValue": "Enabled" }
        ]
      }
    ]
    """;

    // A genuine board+BIOS KB entry — the only shape allowed to be verified (decision §9.1).
    private const string VerifiedBoardJson = """
    [
      {
        "vendor": "Msi",
        "biosUiGeneration": "Click BIOS 5",
        "isVerified": true,
        "board": "MAG B650 TOMAHAWK WIFI",
        "biosVersion": "7D75v18",
        "enterKey": "Del",
        "saveKey": "F10",
        "paths": [
          { "setting": "XmpExpo", "menuPath": "OC", "settingName": "EXPO / A-XMP", "targetValue": "Enabled" }
        ]
      }
    ]
    """;

    [Fact]
    public void Valid_payload_parses()
    {
        var r = Parse(ValidJson);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        r.Value!.Should().ContainSingle();
        r.Value![0].Vendor.Should().Be(BiosVendor.Msi);
        r.Value![0].Paths.Single().Setting.Should().Be(FirmwareSettingKind.XmpExpo);
    }

    [Fact]
    public void Malformed_json_fails_clean()
    {
        var r = Parse("{ not json");
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Unknown_vendor_enum_fails_clean()
    {
        var json = ValidJson.Replace("\"Msi\"", "\"NotAVendor\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Unknown_setting_enum_fails_clean()
    {
        var json = ValidJson.Replace("\"XmpExpo\"", "\"NotASetting\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Empty_setting_name_fails_clean()
    {
        // The exact setting name is the safety net — it can never be blank.
        var json = ValidJson.Replace("\"EXPO / A-XMP\"", "\"\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Missing_enter_key_fails_clean()
    {
        var json = ValidJson.Replace("\"enterKey\": \"Del\",", "");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("{{ inject }}")]
    [InlineData("path & </value>")]
    public void Injection_in_a_value_is_rejected(string payload)
    {
        var json = ValidJson.Replace("\"OC\"", $"\"{payload}\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse("template values must not carry HTML/template injection markers");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Empty_array_fails_clean()
    {
        var r = Parse("[]");
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    // ── HONESTY (decision §9.1): "Verified" is reserved for a board+BIOS KB entry. ──

    [Fact]
    public void Verified_template_with_board_and_version_parses()
    {
        var r = Parse(VerifiedBoardJson);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        var t = r.Value!.Single();
        t.IsVerified.Should().BeTrue();
        t.Board.Should().Be("MAG B650 TOMAHAWK WIFI");
        t.BiosVersion.Should().Be("7D75v18");
    }

    [Fact]
    public void Verified_without_board_or_version_is_rejected()
    {
        // A vendor/UI-generation entry that marks itself verified is a lie → reject.
        var json = ValidJson.Replace("\"isVerified\": false", "\"isVerified\": true");
        var r = Parse(json);
        r.IsOk.Should().BeFalse("a verified template must carry a board+BIOS KB key");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Verified_with_board_but_no_version_is_rejected()
    {
        // Board alone is not the KB key — board + BIOS version both required for verified.
        var json = VerifiedBoardJson.Replace("\"biosVersion\": \"7D75v18\",", "");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    // ── SEMANTIC direction guard: a dangerous wrong-direction mis-author is rejected. ──

    [Fact]
    public void Wrong_direction_xmp_disabled_is_rejected()
    {
        // XMP/EXPO must be enabled — a "Disabled" target is a mis-author.
        var json = ValidJson.Replace("\"targetValue\": \"Enabled\"", "\"targetValue\": \"Disabled\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse("XMP/EXPO must be enabled, not disabled");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Wrong_direction_csm_enabled_is_rejected()
    {
        // CSM must be Disabled/UEFI — an "Enabled" target is a no-boot mis-author.
        const string json = """
        [
          {
            "vendor": "Msi", "biosUiGeneration": "Click BIOS 5", "isVerified": false,
            "enterKey": "Del", "saveKey": "F10",
            "paths": [
              { "setting": "Csm", "menuPath": "Boot", "settingName": "CSM", "targetValue": "Enabled" }
            ]
          }
        ]
        """;
        var r = Parse(json);
        r.IsOk.Should().BeFalse("CSM must be Disabled/UEFI, never Enabled");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("UEFI")]
    public void Correct_csm_direction_passes(string target)
    {
        var json = $$"""
        [
          {
            "vendor": "Msi", "biosUiGeneration": "Click BIOS 5", "isVerified": false,
            "enterKey": "Del", "saveKey": "F10",
            "paths": [
              { "setting": "Csm", "menuPath": "Boot", "settingName": "CSM", "targetValue": "{{target}}" }
            ]
          }
        ]
        """;
        Parse(json).IsOk.Should().BeTrue("CSM off (Disabled/UEFI) is the safe direction");
    }
}
