using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T8-T12 — ClassifyDriftAsync (WindowsReset / UserIntentional / Unknown).
//
// Heuristic (plan §4), not magic:
//   * build CHANGED since apply + current == original (stock)        → WindowsReset
//   * per-vendor DriverVersion CHANGED + GPU tweak drifted           → WindowsReset (T8b/T8c)
//   * build UNCHANGED + current is a THIRD value (≠desired ≠original) → UserIntentional
//   * build UNCHANGED + current == original                          → Unknown (ambiguous)
//   * current == desired                                             → no drift (Unknown, no effect)
//   * probe fails                                                    → Fail (no guess, no write)
// ClassifyDriftAsync never writes.
// -----------------------------------------------------------------------
public sealed class DriftClassificationTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static AppliedTweak Reg(string desired, string original)
        => new("reflex.lowlatency", desired, original, At);

    private static ReconcilerState StateWithItem(string lastBuild, string lastDriver = "")
        => ReconcilerState.Default with
        {
            Items = new[]
            {
                new ReconcilerItemState("reflex.lowlatency", false, lastBuild, lastDriver, At, "OK"),
                new ReconcilerItemState("gpu.drs", false, lastBuild, lastDriver, At, "OK"),
            },
        };

    // T8 — build changed + value is back to original (stock reset pattern).
    [Fact]
    public async Task ClassifyDriftAsync_BuildChanged_ValueIsOriginal_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000";                 // build CHANGED
        h.Probe.Set("reflex.lowlatency", "{\"v\":0}");      // current == original

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // T8b — NVIDIA DriverVersion changed → WindowsReset-class (non-user) cause.
    [Fact]
    public async Task ClassifyDriftAsync_NvidiaDriverVersionChanged_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447", lastDriver: "555.99"));
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";                 // build UNCHANGED
        h.DriverProbe.Set("NVIDIA", "560.00");             // driver CHANGED
        h.Probe.Set("gpu.drs", "0");                        // drifted (≠ desired)

        var cause = await h.Build().ClassifyDriftAsync(
            new AppliedTweak("gpu.drs", "1", "0", At));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // T8c — AMD driver change drives the same non-user cause.
    [Fact]
    public async Task ClassifyDriftAsync_AmdDriverVersionChanged_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447", lastDriver: "24.5.1"));
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "AMD");
        h.BuildProbe.Build = "22631.3447";
        h.DriverProbe.Set("AMD", "24.8.1");                // AMD driver changed
        h.Probe.Set("gpu.drs", "0");

        var cause = await h.Build().ClassifyDriftAsync(new AppliedTweak("gpu.drs", "1", "0", At));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // T9 — no build change + a foreign (third) value → user did something.
    [Fact]
    public async Task ClassifyDriftAsync_NoBuildChange_ValueIsForeign_ReturnsUserIntentional()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";                 // build UNCHANGED
        h.Probe.Set("reflex.lowlatency", "{\"v\":1}");      // ≠ desired AND ≠ original

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.UserIntentional);
    }

    // T10 — no build change + value is exactly original → ambiguous (Unknown).
    [Fact]
    public async Task ClassifyDriftAsync_NoBuildChange_ValueIsOriginal_ReturnsUnknown()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";
        h.Probe.Set("reflex.lowlatency", "{\"v\":0}");      // == original, no build change

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.Unknown);
    }

    // T11 — value == desired → no drift; returns a neutral cause, NO side effect.
    [Fact]
    public async Task ClassifyDriftAsync_ValueMatchesDesired_ReturnsNoDrift_NoSideEffect()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";
        h.Probe.Set("reflex.lowlatency", "{\"v\":2}");      // == desired

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.Unknown);
        h.Reapply.Reapplies.Should().BeEmpty("classify never writes");
        h.Diagnostics.Entries.Should().BeEmpty();
    }

    // T12 — probe failure → fail-clean, no guess, no write.
    [Fact]
    public async Task ClassifyDriftAsync_WhenProbeFails_ReturnsFail()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";
        h.Probe.FailRead("reflex.lowlatency");

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeFalse();
        cause.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ProbeFailed);
        h.Reapply.Reapplies.Should().BeEmpty();
    }
}
