using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Validation défensive des données de la bibliothèque de guides (valider toute donnée externe
/// avant usage). Une charge valide s'analyse ; une corrompue (champ manquant, mauvaise énumération,
/// nom de réglage vide, injection HTML ou de gabarit dans une valeur) échoue proprement avec
/// <see cref="ErrorCodes.BiosTemplateInvalid"/>, jamais une exception.
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

    // Une vraie entrée carte + BIOS de la base : la seule forme autorisée à être vérifiée.
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
        // Le nom exact du réglage est le filet de sécurité : il ne peut jamais être vide.
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

    // ── Honnêteté : « vérifié » est réservé à une entrée carte + BIOS de la base. ──

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
        // Une entrée constructeur/génération qui se dit vérifiée est un mensonge → rejet.
        var json = ValidJson.Replace("\"isVerified\": false", "\"isVerified\": true");
        var r = Parse(json);
        r.IsOk.Should().BeFalse("a verified template must carry a board+BIOS KB key");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    [Fact]
    public void Verified_with_board_but_no_version_is_rejected()
    {
        // La carte seule n'est pas la clé de la base : carte + version de BIOS requises pour vérifié.
        var json = VerifiedBoardJson.Replace("\"biosVersion\": \"7D75v18\",", "");
        var r = Parse(json);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    // ── Garde sémantique de direction : une erreur d'auteur dangereuse dans le mauvais sens est rejetée. ──

    [Fact]
    public void Wrong_direction_xmp_disabled_is_rejected()
    {
        // XMP/EXPO doit être activé : une cible « Disabled » est une erreur d'auteur.
        var json = ValidJson.Replace("\"targetValue\": \"Enabled\"", "\"targetValue\": \"Disabled\"");
        var r = Parse(json);
        r.IsOk.Should().BeFalse("XMP/EXPO must be enabled, not disabled");
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosTemplateInvalid);
    }

    // Le nom du réglage dans la cible ne la rend pas juste : « XMP Disabled » contient « xmp ».
    [Theory]
    [InlineData("XMP Disabled")]
    [InlineData("Profile Disabled")]
    [InlineData("EXPO Disabled")]
    public void Wrong_direction_xmp_disabled_is_rejected_even_when_named(string cible)
        => Parse(ValidJson.Replace("\"targetValue\": \"Enabled\"", $"\"targetValue\": \"{cible}\"")).IsOk.Should().BeFalse();

    [Fact]
    public void Wrong_direction_csm_enabled_is_rejected()
    {
        // Le CSM doit être Disabled/UEFI : une cible « Enabled » est une erreur d'auteur qui empêche le démarrage.
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
