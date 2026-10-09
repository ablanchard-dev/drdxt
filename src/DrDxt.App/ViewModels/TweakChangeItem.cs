namespace DrDxt.App.ViewModels;

/// <summary>
/// A single row in the dashboard "changes" list. Carries only resx KEYS (resolved
/// to localized text in the view) so the VM stays string-free and testable — the
/// same key-not-text discipline the rest of the app uses.
/// </summary>
/// <param name="TweakId">The engine tweak id (e.g. <c>latency.reflex</c>).</param>
/// <param name="TitleKey">Resx key for the human-readable tweak title.</param>
/// <param name="SummaryKey">Resx key for the honest one-line effect summary.</param>
/// <param name="WasReverted">True when the tweak was auto-reverted (vs applied-and-kept).</param>
public sealed record TweakChangeItem(string TweakId, string TitleKey, string SummaryKey, bool WasReverted);
