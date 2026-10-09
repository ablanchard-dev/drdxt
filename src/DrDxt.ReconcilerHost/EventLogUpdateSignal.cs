using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// EventLogUpdateSignal — the production IUpdateSignal.
//
// The corroborating value comes from a recent successful Windows Update installation in
// the event log (WindowsUpdateClient, read by `WindowsUpdateEventLog`, the same reader the
// health audit uses). The primary build detector stays authoritative; this signal only ever
// ADDS an earlier fire, and a read failure is a `Fail`, which the trigger treats as "no extra
// signal" (FeatureUpdateTrigger). It never throws.
//
// 25/09/2026 : Defender definition updates are excluded. Windows Update installs them several
// times a day and they touch no setting DrDXT holds; counting them would make every day look
// like an update. « Defender » is a brand name, identical in every Windows language.
// -----------------------------------------------------------------------
public sealed class EventLogUpdateSignal : IUpdateSignal
{
    internal static readonly TimeSpan Fenetre = TimeSpan.FromHours(24);

    private readonly IWindowsUpdateLog _journal;
    private readonly Func<DateTimeOffset> _maintenant;

    public EventLogUpdateSignal() : this(new WindowsUpdateEventLog(), () => DateTimeOffset.UtcNow) { }

    internal EventLogUpdateSignal(IWindowsUpdateLog journal, Func<DateTimeOffset> maintenant)
        => (_journal, _maintenant) = (journal, maintenant);

    public Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct)
    {
        try
        {
            var lu = _journal.Depuis(_maintenant() - Fenetre);
            if (!lu.IsOk)
                return Task.FromResult(Result<bool>.Fail(lu.Error!.Value.Code, lu.Error.Value.Message));
            var recente = lu.Value!.Any(e => e.Reussi
                && !e.Titre.Contains("Defender", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Result<bool>.Ok(recente));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Fail(ErrorCodes.DetectionFailed, ex.Message));
        }
    }
}
