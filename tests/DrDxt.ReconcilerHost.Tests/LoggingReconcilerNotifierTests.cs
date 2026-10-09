using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// LoggingReconcilerNotifier — the host IReconcilerNotifier. A UserIntentional/Unknown
// drift is NOTIFIED (logged), never overwritten (I5). i18n: the message comes from a
// resx KEY via ILocalizer (no hardcoded UI string), with the tweak id as the argument.
// -----------------------------------------------------------------------
public sealed class LoggingReconcilerNotifierTests
{
    private static readonly AppliedTweak Tweak =
        new("latency.reflex.lowLatencyMode", "2", "0", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Notify_JournalsTheDrift_ViaDiagnostics()
    {
        var diagnostics = new RecordingDiagnostics();
        var notifier = new LoggingReconcilerNotifier(diagnostics, new FakeLocalizer());

        await notifier.NotifyUserIntentionalDriftAsync(Tweak, "5", default);

        diagnostics.Entries.Should().ContainSingle();
        var entry = diagnostics.Entries[0];
        entry.tweakId.Should().Be("latency.reflex.lowLatencyMode");
        entry.to.Should().Be("5", "the current (user-set) value is recorded");
        entry.actor.Should().Be("DrDXT/notified");
    }

    [Fact]
    public async Task Notify_ResolvesMessage_ThroughLocalizerKey_NotHardcoded()
    {
        var localizer = new FakeLocalizer();
        var notifier = new LoggingReconcilerNotifier(new RecordingDiagnostics(), localizer);

        await notifier.NotifyUserIntentionalDriftAsync(Tweak, "5", default);

        localizer.RequestedKeys.Should().Contain("reconciler.notify.user_drift",
            "the notification text comes from a resx key, never a hardcoded string");
    }

    /// <summary>A localizer that records requested keys and echoes the key (the resx
    /// fallback contract) so the test never depends on satellite assemblies.</summary>
    private sealed class FakeLocalizer : ILocalizer
    {
        public List<string> RequestedKeys { get; } = new();

        public string Get(string key, CultureInfo? culture = null)
        {
            RequestedKeys.Add(key);
            return key;
        }
    }
}
