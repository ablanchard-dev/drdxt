using System;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// A localized, display-ready BIOS Doctor finding (M3) surfaced in the scan results.
///
/// BIOS findings are GUIDANCE-only: DrDXT never writes the firmware (decision §2, V1 =
/// read + guide + prove). The user changes the setting themselves in the BIOS — these
/// findings are never added to the apply batch. This VM carries the honest title,
/// rationale, recommended direction, a SOURCED gain RANGE with its caveat (never a bare
/// promised %), and honest flags:
///   • <see cref="IsGuidanceOnly"/> — an actionable change the user makes in the BIOS;
///   • <see cref="StateNotReadable"/> — the state could not be read, so we say "to verify"
///     rather than assert a false state (honesty, decision §9 / §22.3).
/// </summary>
public sealed class BiosFindingViewModel
{
    public BiosFindingViewModel(BiosFinding f, Func<string, string> localize)
    {
        Kind = f.Kind.ToString();
        Severity = f.Severity;
        Title = localize(f.TitleKey);
        Constat = DrDxt.Core.Constat.Depuis(f);
        Rationale = localize(f.RationaleKey);
        ActionLabel = localize(f.RecommendedActionKey);

        // Guidance-only: every actionable BIOS fix is a GUIDE — we never write the firmware.
        IsGuidanceOnly = f.MatrixClass == BiosMatrixClass.Guide;
        GuidanceLabel = IsGuidanceOnly ? localize("Scan.Bios.GuidanceOnly") : string.Empty;

        // Honest "not read": surface "to verify" instead of asserting a state we don't have.
        StateNotReadable = f.StateNotReadable;
        UnverifiedLabel = f.StateNotReadable ? localize("UI.Profile.Unverified") : string.Empty;

        if (f.Gain is { } g)
        {
            // A SOURCED range + caveat (never a bare promised point-%). An unquantified gain
            // (e.g. game-dependent ReBAR: null/null) leans entirely on the caveat — no number.
            GainLabel = (g.MinPct is { } min && g.MaxPct is { } max)
                ? FormatRange(localize("Scan.Bios.GainRange"), min, max)
                : string.Empty;
            GainSource = localize(g.SourceKey);
            GainCaveat = localize(g.CaveatKey);
            IsGainConditional = g.Conditional;
        }
        else
        {
            GainLabel = string.Empty;
            GainSource = string.Empty;
            GainCaveat = string.Empty;
        }
    }

    /// <summary>chantier B0 : la forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Kind { get; }
    public Severity Severity { get; }
    public string Title { get; }
    public string Rationale { get; }
    public string ActionLabel { get; }


    /// <summary>A SOURCED gain range (e.g. "≈ 11–25%"); empty when unquantified.</summary>
    public string GainLabel { get; }
    public string GainSource { get; }
    public string GainCaveat { get; }

    /// <summary>True when the gain is realized only if the setting is actually mis-set/mis-routed
    /// (likely ≈0 otherwise) — drives conditional UI framing so we never over-promise.</summary>
    public bool IsGainConditional { get; }

    /// <summary>True for an actionable change the user makes in the BIOS (DrDXT never writes it).</summary>
    public bool IsGuidanceOnly { get; }
    public string GuidanceLabel { get; }

    /// <summary>True when the current state could not be read (honest unknown → "to verify").</summary>
    public bool StateNotReadable { get; }
    public string UnverifiedLabel { get; }

    private static string FormatRange(string template, double min, double max)
    {
        var lo = min.ToString("0.#", CultureInfo.InvariantCulture);
        var hi = max.ToString("0.#", CultureInfo.InvariantCulture);
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, lo, hi)
            : $"≈ {lo}–{hi}%";
    }
}
