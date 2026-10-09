using System.Reflection;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Data-driven guide library: the curated, validated <see cref="GuideTemplate"/>s.
/// A genuine KB entry is keyed by <b>board + BIOS version</b> (decision §9.1) and is the
/// Only thing that resolves as <see cref="GuideTemplate.IsVerified"/>; the per-vendor ×
/// BIOS-UI generation and generic entries are best-known FALLBACKS only.
///
/// PRECISION = the product (decision §9): a covered board + BIOS version → exact verified
/// template; anything else → a labelled fallback (<see cref="GuideTemplate.IsVerified"/> =
/// false) that still carries the exact setting names. <see cref="Resolve"/> never
/// strands the user — it only fails on internally corrupt library data, which is
/// rejected at construction by <see cref="GuideTemplateValidator"/>.
/// </summary>
public sealed class GuideLibrary : IGuideLibrary
{
    private readonly IReadOnlyList<GuideTemplate> _templates;

    /// <summary>
    /// Builds a library from already-validated templates. Use
    /// <see cref="GuideLibraryFactory"/> to load the embedded data defensively.
    /// </summary>
    public GuideLibrary(IReadOnlyList<GuideTemplate> templates)
        => _templates = templates ?? throw new ArgumentNullException(nameof(templates));

    /// <inheritdoc/>
    public Result<GuideTemplate> Resolve(
        BiosVendor vendor, string biosUiGeneration, string? board = null, string? biosVersion = null)
    {
        var gen = string.IsNullOrWhiteSpace(biosUiGeneration)
            ? BiosUiGenerationDetector.Generic
            : biosUiGeneration.Trim();

        // 0) The board + BIOS-version KB hit — the only match that can be Verified
        //    (decision §9.1). A vendor/UI-generation entry never carries a board key,
        //    so it can never be returned here, and so can never claim "verified for your
        //    exact board". When the KB has no entry for this board (the current state),
        //    we fall through to the honest best-known fallbacks below.
        if (!string.IsNullOrWhiteSpace(board) && !string.IsNullOrWhiteSpace(biosVersion))
        {
            var b = board.Trim();
            var v = biosVersion.Trim();
            var boardExact = _templates.FirstOrDefault(t =>
                t.Vendor == vendor &&
                !string.IsNullOrWhiteSpace(t.Board) &&
                !string.IsNullOrWhiteSpace(t.BiosVersion) &&
                string.Equals(t.Board, b, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.BiosVersion, v, StringComparison.OrdinalIgnoreCase));
            if (boardExact is not null)
                return Result<GuideTemplate>.Ok(boardExact);
        }

        // 1) Best-known entry for this vendor × UI generation (a labelled fallback —
        //    Never verified; the menu paths are the best available, not board-confirmed).
        var exact = _templates.FirstOrDefault(t =>
            t.Vendor == vendor &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, gen, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return Result<GuideTemplate>.Ok(exact);

        // 2) Per-vendor generic fallback (labelled unverified).
        var vendorGeneric = _templates.FirstOrDefault(t =>
            t.Vendor == vendor &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, BiosUiGenerationDetector.Generic, StringComparison.OrdinalIgnoreCase));
        if (vendorGeneric is not null)
            return Result<GuideTemplate>.Ok(vendorGeneric);

        // 3) Cross-vendor generic fallback (most prudent; Unknown × Generic).
        var generic = _templates.FirstOrDefault(t =>
            t.Vendor == BiosVendor.Unknown &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, BiosUiGenerationDetector.Generic, StringComparison.OrdinalIgnoreCase));
        if (generic is not null)
            return Result<GuideTemplate>.Ok(generic);

        // The embedded library always ships an Unknown×Generic entry, so reaching
        // here means the data is internally incomplete → fail clean, never strand
        // the caller with a null template.
        return Result<GuideTemplate>.Fail(
            ErrorCodes.BiosTemplateInvalid, "guide library is missing the generic fallback template");
    }
}

/// <summary>
/// Loads the embedded guide data (<c>data/guides.json</c>) and builds a validated
/// <see cref="GuideLibrary"/>. Defensive: a missing/corrupt embedded resource throws
/// at startup wiring (a packaging bug we want loud), but the JSON content itself is
/// validated via <see cref="GuideTemplateValidator"/> before use.
/// </summary>
public static class GuideLibraryFactory
{
    private const string ResourceName = "DrDxt.Bios.data.guides.json";

    /// <summary>Loads &amp; validates the embedded library; throws only on a packaging fault.</summary>
    public static GuideLibrary CreateDefault()
    {
        var json = ReadEmbedded();
        var parsed = GuideTemplateValidator.ParseAndValidate(json);
        if (!parsed.IsOk)
            throw new InvalidOperationException(
                $"Embedded guide library is invalid ({parsed.Error!.Value.Code}): {parsed.Error!.Value.Message}");
        return new GuideLibrary(parsed.Value!);
    }

    /// <summary>Loads the embedded library as a <see cref="Result"/> (no throw on bad data).</summary>
    public static Result<GuideLibrary> TryCreateDefault()
    {
        string json;
        try
        {
            json = ReadEmbedded();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            return Result<GuideLibrary>.Fail(ErrorCodes.BiosTemplateInvalid, ex.Message);
        }

        var parsed = GuideTemplateValidator.ParseAndValidate(json);
        if (!parsed.IsOk)
            return Result<GuideLibrary>.Fail(parsed.Error!.Value.Code, parsed.Error!.Value.Message);
        return Result<GuideLibrary>.Ok(new GuideLibrary(parsed.Value!));
    }

    private static string ReadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded guide resource '{ResourceName}' not found.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
