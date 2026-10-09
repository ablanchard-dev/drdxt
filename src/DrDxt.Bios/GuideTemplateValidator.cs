using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Parses + defensively validates the guide-library JSON (§9.6: validate ALL external
/// data before it is ever used). Library data is a moat asset; a corrupt or tampered
/// file must fail cleanly (<see cref="ErrorCodes.BiosTemplateInvalid"/>) — never crash,
/// never feed an unvalidated value into a guide.
///
/// Validated invariants:
///   • well-formed JSON array, at least one template;
///   • vendor + every setting parse to a KNOWN enum (no fallback to enum 0);
///   • a template may only be <c>isVerified</c> when it carries a board + BIOS-version
///     KB key (decision §9.1 — a vendor/UI-generation entry can never be "verified");
///   • enter/save keys are present;
///   • every path's exact <c>settingName</c> is non-blank (it is the safety net — the
///     client must always get a name to search for, even on a fallback);
///   • each path's <c>targetValue</c> points the SAFE/intended direction for its setting
///     (CSM off/UEFI; XMP/EXPO, ReBAR, Above-4G enabled; X3D CPPC Auto/Enabled) — a
///     wrong-direction mis-author (e.g. <c>Csm="Enabled"</c>) is rejected;
///   • no value carries an HTML/template injection marker (defence-in-depth for the
///     later rendering increment).
/// </summary>
public static class GuideTemplateValidator
{
    // Markers that must never appear in library data. The values are plain BIOS menu
    // labels/keys; '>' is deliberately not here because it is the legitimate menu-path
    // separator ("Advanced > PCI Subsystem"). The markers below catch HTML tags and
    // template-engine syntax — i.e. tampering or mis-authoring, not normal labels.
    private static readonly string[] InjectionMarkers =
        { "<", "{{", "}}", "&lt;", "&gt;", "${" };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parses <paramref name="json"/> into validated templates, or fails clean.
    /// </summary>
    public static Result<IReadOnlyList<GuideTemplate>> ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("library JSON is empty");

        List<TemplateDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<TemplateDto>>(json, Options);
        }
        catch (JsonException ex)
        {
            return Fail($"malformed JSON: {ex.Message}");
        }

        if (dtos is null || dtos.Count == 0)
            return Fail("no templates in library payload");

        var templates = new List<GuideTemplate>(dtos.Count);
        foreach (var dto in dtos)
        {
            var built = Validate(dto);
            if (!built.IsOk)
                return Result<IReadOnlyList<GuideTemplate>>.Fail(built.Error!.Value.Code, built.Error!.Value.Message);
            templates.Add(built.Value!);
        }

        return Result<IReadOnlyList<GuideTemplate>>.Ok(templates);
    }

    private static Result<GuideTemplate> Validate(TemplateDto dto)
    {
        if (dto.Vendor is null || !Enum.TryParse<BiosVendor>(dto.Vendor, ignoreCase: false, out var vendor))
            return FailT($"unknown or missing vendor '{dto.Vendor}'");
        if (IsBlankOrInjected(dto.BiosUiGeneration))
            return FailT("missing/invalid biosUiGeneration");
        if (IsBlankOrInjected(dto.EnterKey))
            return FailT("missing/invalid enterKey");
        if (IsBlankOrInjected(dto.SaveKey))
            return FailT("missing/invalid saveKey");
        if (dto.Paths is null || dto.Paths.Count == 0)
            return FailT("template has no setting paths");

        // Board + BIOS version are OPTIONAL (a vendor/UI-generation fallback omits them),
        // but if present they must be clean. Together they form the board+BIOS KB key.
        var board = dto.Board ?? string.Empty;
        if (ContainsInjection(board))
            return FailT("injection in board");
        var biosVersion = dto.BiosVersion ?? string.Empty;
        if (ContainsInjection(biosVersion))
            return FailT("injection in biosVersion");

        // HONESTY (decision §9.1): "Verified for your exact board" may only be claimed by
        // a real board + BIOS-version KB entry. A vendor / UI-generation entry that marks
        // itself verified is a lie (and a tamper/mis-author vector) → reject it.
        if (dto.IsVerified && (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(biosVersion)))
            return FailT("a verified template must carry both a board and a biosVersion "
                + "(the board+BIOS KB key); vendor/UI-generation entries cannot be verified");

        var paths = new List<GuideSettingPath>(dto.Paths.Count);
        var seen = new HashSet<FirmwareSettingKind>();
        foreach (var p in dto.Paths)
        {
            if (p.Setting is null || !Enum.TryParse<FirmwareSettingKind>(p.Setting, ignoreCase: false, out var setting))
                return FailT($"unknown or missing setting '{p.Setting}'");
            if (!seen.Add(setting))
                return FailT($"duplicate setting '{setting}' in template");

            // MenuPath MAY be empty (a name-only fallback); but if present it must be clean.
            var menuPath = p.MenuPath ?? string.Empty;
            if (ContainsInjection(menuPath))
                return FailT($"injection in menuPath for '{setting}'");

            // SettingName is the safety net — it can never be blank, and must be clean.
            if (IsBlankOrInjected(p.SettingName))
                return FailT($"missing/invalid settingName for '{setting}'");
            if (IsBlankOrInjected(p.TargetValue))
                return FailT($"missing/invalid targetValue for '{setting}'");

            // SEMANTIC direction guard: a structurally valid template could still be
            // mis-authored in a DANGEROUS direction (e.g. Csm="Enabled" → no-boot risk,
            // or XmpExpo="Disabled"). Assert the target points the safe/intended way.
            var targetValue = p.TargetValue!.Trim();
            if (!IsExpectedDirection(setting, targetValue))
                return FailT($"targetValue '{targetValue}' for '{setting}' points the wrong direction "
                    + "(CSM must be Disabled/UEFI; XMP/EXPO, ReBAR and Above-4G must be enabled; "
                    + "X3D CPPC must be Auto/Enabled)");

            paths.Add(new GuideSettingPath(setting, menuPath, p.SettingName!.Trim(), targetValue));
        }

        return Result<GuideTemplate>.Ok(new GuideTemplate(
            vendor, dto.BiosUiGeneration!.Trim(), dto.IsVerified,
            board.Trim(), biosVersion.Trim(),
            dto.EnterKey!.Trim(), dto.SaveKey!.Trim(), paths));
    }

    // Per-setting expected target DIRECTION. Catches a dangerous mis-author that passes
    // every structural check (e.g. a CSM target of "Enabled", or an XMP target of
    // "Disabled"). Tokens are matched case-insensitively against the target value.
    private static bool IsExpectedDirection(FirmwareSettingKind setting, string targetValue)
    {
        var v = targetValue.ToLowerInvariant();
        bool Any(params string[] tokens) => tokens.Any(t => v.Contains(t, StringComparison.Ordinal));
        return setting switch
        {
            // The memory profile must be turned ON (enable / a profile / EXPO / XMP / DOCP).
            FirmwareSettingKind.XmpExpo => Any("enable", "profile", "expo", "xmp", "docp"),
            // ReBAR and its Above-4G prerequisite must be ENABLED.
            FirmwareSettingKind.ReBar => Any("enable"),
            FirmwareSettingKind.Above4G => Any("enable"),
            // CSM must be turned OFF (Disabled) or the boot mode set to UEFI — never
            // enabled (keeping CSM defeats ReBAR and is on the no-boot path).
            FirmwareSettingKind.Csm => Any("disable", "uefi"),
            // The X3D CPPC route must be the supported one (Auto / Enabled / Driver).
            FirmwareSettingKind.X3dCppc => Any("auto", "enable", "driver"),
            // Other settings carry no enforced direction in the foundation.
            _ => true,
        };
    }

    private static bool IsBlankOrInjected(string? s)
        => string.IsNullOrWhiteSpace(s) || ContainsInjection(s);

    private static bool ContainsInjection(string s)
        => InjectionMarkers.Any(m => s.Contains(m, StringComparison.Ordinal));

    private static Result<IReadOnlyList<GuideTemplate>> Fail(string why)
        => Result<IReadOnlyList<GuideTemplate>>.Fail(ErrorCodes.BiosTemplateInvalid, why);

    private static Result<GuideTemplate> FailT(string why)
        => Result<GuideTemplate>.Fail(ErrorCodes.BiosTemplateInvalid, why);

    // DTOs are deliberately nullable so a missing field is caught by validation, not
    // by a deserialization default that silently produces an enum-0 / empty string.
    private sealed class TemplateDto
    {
        public string? Vendor { get; init; }
        public string? BiosUiGeneration { get; init; }
        public bool IsVerified { get; init; }
        public string? Board { get; init; }
        public string? BiosVersion { get; init; }
        public string? EnterKey { get; init; }
        public string? SaveKey { get; init; }
        public List<PathDto>? Paths { get; init; }
    }

    private sealed class PathDto
    {
        public string? Setting { get; init; }
        public string? MenuPath { get; init; }
        public string? SettingName { get; init; }
        public string? TargetValue { get; init; }
    }
}
