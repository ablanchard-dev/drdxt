using System;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Un constat du docteur BIOS, traduit et prêt à afficher dans les résultats du scan.
///
/// Les constats BIOS ne sont que du guidage : DrDXT n'écrit jamais le firmware (V1 = lire,
/// guider, prouver). Le joueur change lui-même le réglage dans le BIOS ; ces constats ne sont
/// jamais ajoutés au lot appliqué. Ce VM porte le titre honnête, la raison, le sens conseillé,
/// une plage de gain sourcée avec sa réserve (jamais un % promis tout seul), et des drapeaux
/// honnêtes :
///   • <see cref="IsGuidanceOnly"/> : un changement à faire par le joueur dans le BIOS ;
///   • <see cref="StateNotReadable"/> : l'état n'a pas pu être lu, on dit donc « à vérifier »
///     au lieu d'affirmer un état faux (honnêteté).
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

        // Guidage seul : chaque correction BIOS à faire est un guide, on n'écrit jamais le firmware.
        IsGuidanceOnly = f.MatrixClass == BiosMatrixClass.Guide;
        GuidanceLabel = IsGuidanceOnly ? localize("Scan.Bios.GuidanceOnly") : string.Empty;

        // « Non lu » honnête : on affiche « à vérifier » au lieu d'affirmer un état qu'on n'a pas.
        StateNotReadable = f.StateNotReadable;
        UnverifiedLabel = f.StateNotReadable ? localize("UI.Profile.Unverified") : string.Empty;

        if (f.Gain is { } g)
        {
            // Une plage sourcée et sa réserve (jamais un % ponctuel promis tout seul). Un gain non chiffré
            // (par exemple ReBAR, qui dépend du jeu : null/null) repose entièrement sur la réserve, sans chiffre.
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

    /// <summary>La forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Kind { get; }
    public Severity Severity { get; }
    public string Title { get; }
    public string Rationale { get; }
    public string ActionLabel { get; }


    /// <summary>Une plage de gain sourcée (par exemple « ≈ 11–25% ») ; vide si non chiffrée.</summary>
    public string GainLabel { get; }
    public string GainSource { get; }
    public string GainCaveat { get; }

    /// <summary>Vrai quand le gain n'existe que si le réglage est vraiment mal posé ou mal routé
    /// (sinon probablement ≈0) : pilote une formulation conditionnelle dans l'UI, pour ne jamais trop promettre.</summary>
    public bool IsGainConditional { get; }

    /// <summary>Vrai pour un changement à faire par le joueur dans le BIOS (DrDXT ne l'écrit jamais).</summary>
    public bool IsGuidanceOnly { get; }
    public string GuidanceLabel { get; }

    /// <summary>Vrai quand l'état actuel n'a pas pu être lu (inconnu honnête → « à vérifier »).</summary>
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
