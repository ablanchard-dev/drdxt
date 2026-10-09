using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Shared builder for the Reconciler under test, wiring all in-memory fakes. Each
// test mutates the pieces it cares about. Nothing here touches the real machine.
// -----------------------------------------------------------------------
public sealed class ReconcilerHarness
{
    public FakeSettingProbe Probe { get; } = new();
    public FakeOsBuildProbe BuildProbe { get; } = new();
    public FakeGpuDriverProbe DriverProbe { get; } = new();
    public InMemoryReconcilerStateStore StateStore { get; }
    public RecordingNotifier Notifier { get; } = new();
    public RecordingDiagnostics Diagnostics { get; } = new();
    public FakePolicy Policy { get; } = new();
    public FakeReapplyClient Reapply { get; private set; }
    public FakeBootOutcomeStore BootStore { get; } = new();
    public FakeClock Clock { get; } = new();

    public int BootLoopThreshold { get; set; } = 3;

    public ReconcilerHarness(ReconcilerState? initialState = null)
    {
        StateStore = new InMemoryReconcilerStateStore(initialState);
        Reapply = new FakeReapplyClient(Probe);
    }

    public ReconcilerHarness WithReapply(FakeReapplyClient client)
    {
        Reapply = client;
        return this;
    }

    public DesiredStateReconciler Build()
    {
        var guard = new BootLoopGuard(BootStore, BootLoopThreshold);
        return new DesiredStateReconciler(
            Probe, BuildProbe, DriverProbe, StateStore, Reapply, Notifier,
            Policy, Diagnostics, guard, Clock);
    }
}
