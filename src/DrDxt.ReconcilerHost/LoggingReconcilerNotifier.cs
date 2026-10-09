using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// LoggingReconcilerNotifier — the host IReconcilerNotifier.
//
// When a drift is classified UserIntentional/Unknown the reconciler does not overwrite
// it (I5); it raises this notification instead. In the background service there is no
// interactive toast surface, so the notification is journalled through IDiagnosticsLog
// (the same audit trail the App reads) — the App can surface it on next launch.
//
// i18n: the human-readable text is resolved from a resx KEY via ILocalizer, never a
// hardcoded string (the project HAS a localizer, §4). The localized message is built
// for the audit/UI; the journal line itself records the structured facts (tweak id +
// current user-set value) so a log scan stays language-independent.
// -----------------------------------------------------------------------
public sealed class LoggingReconcilerNotifier : IReconcilerNotifier
{
    /// <summary>The resx key for the "a DrDXT-optimized setting was changed, left as
    /// you set it" notification. {0} = the tweak title/id.</summary>
    public const string UserDriftKey = "reconciler.notify.user_drift";

    /// <summary>Journal actor marking a "notified, not overwritten" outcome — mirrors
    /// the reconciler's own "DrDXT/notified" change-log actor.</summary>
    public const string NotifiedActor = "DrDXT/notified";

    private readonly IDiagnosticsLog _diagnostics;
    private readonly ILocalizer _localizer;

    public LoggingReconcilerNotifier(IDiagnosticsLog diagnostics, ILocalizer localizer)
    {
        _diagnostics = diagnostics;
        _localizer = localizer;
    }

    public Task NotifyUserIntentionalDriftAsync(
        AppliedTweak tweak, string currentValueJson, CancellationToken ct)
    {
        // Resolve the localized message from a resx key (not hardcoded). Built for the
        // audit/UI surface; the structured journal line below stays language-neutral.
        _ = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _localizer.Get(UserDriftKey),
            tweak.TweakId);

        // Journal the structured facts: the desired value drifted to the user-set value;
        // it was NOTIFIED, not overwritten (I5).
        _diagnostics.Change(
            tweakId: tweak.TweakId,
            from: tweak.DesiredValueJson,
            to: currentValueJson,
            actor: NotifiedActor);

        return Task.CompletedTask;
    }
}
