namespace DrDxt.App.ViewModels;

/// <summary>
/// One of the four marketing feature cards shown on the idle screen
/// (Diagnostic / Gaming / Security / Report). Inert in M1 ("En savoir plus" → coming soon).
/// </summary>
/// <param name="IconKey">Stable glyph key.</param>
/// <param name="Title">Localized card title.</param>
/// <param name="Body">Localized one-line description.</param>
public sealed record FeatureItem(string IconKey, string Title, string Body);
