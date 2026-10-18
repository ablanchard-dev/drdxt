using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Constructeur partagé du Reconciler sous test, qui câble tous les faux en mémoire. Chaque
// test mute les pièces qui le concernent. Rien ici ne touche la vraie machine.
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

    // La pause du maintien vit dans un dossier temporaire propre au banc, sans vérification de confiance
    // sauf si le test la demande.
    public string DossierPause { get; } = Path.Combine(Path.GetTempPath(), "drdxt-pause-" + Guid.NewGuid().ToString("N"));
    public bool VerifierConfiancePause { get; set; }
    public PauseDuMaintien Pause => new(DossierPause, VerifierConfiancePause);
    public IReconcilerStateStore? StoreOverride { get; set; }

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
            Probe, BuildProbe, DriverProbe, StoreOverride ?? StateStore, Reapply, Notifier,
            Policy, Diagnostics, guard, Clock, Pause);
    }
}
