using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// ClassifyDriftAsync (WindowsReset / UserIntentional / Unknown).
//
// Une heuristique, pas de la magie :
//   * build changé depuis l'application + courant == origine (stock)     → WindowsReset
//   * DriverVersion du constructeur changé + levier GPU dérivé            → WindowsReset
//   * build inchangé + courant est une troisième valeur (≠voulu ≠origine) → UserIntentional
//   * build inchangé + courant == origine                                 → Unknown (ambigu)
//   * courant == voulu                                                    → pas de dérive (Unknown, sans effet)
//   * la sonde échoue                                                     → Fail (ni supposition ni écriture)
// ClassifyDriftAsync n'écrit jamais.
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

    // Build changé + valeur revenue à l'origine (motif de remise à zéro).
    [Fact]
    public async Task ClassifyDriftAsync_BuildChanged_ValueIsOriginal_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000";                 // build changé
        h.Probe.Set("reflex.lowlatency", "{\"v\":0}");      // courant == origine

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // Le joueur avait déjà remis la valeur d'origine lui-même (la passe précédente l'a notifié) : après une mise à jour de
    // Windows, la même valeur d'origine est son choix, pas une remise à zéro. Le service ne la réécrit pas.
    [Fact]
    public async Task ClassifyDriftAsync_BuildChanged_ValueIsOriginal_AlreadyNotifiedBefore_ReturnsUserIntentional()
    {
        var state = ReconcilerState.Default with
        {
            Items = new[] { new ReconcilerItemState("reflex.lowlatency", false, "22631.3447", "", At, nameof(ItemOutcome.Notified)) },
        };
        var h = new ReconcilerHarness(state);
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000";
        h.Probe.Set("reflex.lowlatency", "{\"v\":0}");

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.Value.Should().Be(DriftCause.UserIntentional);
    }

    // NVIDIA DriverVersion changed → WindowsReset-class (non-user) cause.
    [Fact]
    public async Task ClassifyDriftAsync_NvidiaDriverVersionChanged_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447", lastDriver: "555.99"));
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";                 // build inchangé
        h.DriverProbe.Set("NVIDIA", "560.00");             // pilote changé
        h.Probe.Set("gpu.drs", "0");                        // dérivé (≠ voulu)

        var cause = await h.Build().ClassifyDriftAsync(
            new AppliedTweak("gpu.drs", "1", "0", At));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // Un changement de pilote AMD mène à la même cause non utilisateur.
    [Fact]
    public async Task ClassifyDriftAsync_AmdDriverVersionChanged_ReturnsWindowsReset()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447", lastDriver: "24.5.1"));
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "AMD");
        h.BuildProbe.Build = "22631.3447";
        h.DriverProbe.Set("AMD", "24.8.1");                // pilote AMD changé
        h.Probe.Set("gpu.drs", "0");

        var cause = await h.Build().ClassifyDriftAsync(new AppliedTweak("gpu.drs", "1", "0", At));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.WindowsReset);
    }

    // Un nouveau pilote et une TROISIÈME valeur (ni celle de DrDXT ni l'origine) : c'est le joueur, ou un défaut du pilote
    // qu'on ne sait pas distinguer ; dans le doute, sa valeur est respectée (elle était écrasée comme une remise à zéro).
    [Fact]
    public async Task ClassifyDriftAsync_DriverChanged_ValueIsForeign_ReturnsUserIntentional()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447", lastDriver: "555.99"));
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";
        h.DriverProbe.Set("NVIDIA", "560.00");
        h.Probe.Set("gpu.drs", "3");                        // ni désirée (1) ni origine (0)

        var cause = await h.Build().ClassifyDriftAsync(new AppliedTweak("gpu.drs", "1", "0", At));

        cause.Value.Should().Be(DriftCause.UserIntentional);
    }

    // Pas de changement de build + une valeur étrangère (troisième) → le joueur a fait quelque chose.
    [Fact]
    public async Task ClassifyDriftAsync_NoBuildChange_ValueIsForeign_ReturnsUserIntentional()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";                 // build inchangé
        h.Probe.Set("reflex.lowlatency", "{\"v\":1}");      // ≠ voulu et ≠ origine

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.UserIntentional);
    }

    // Pas de changement de build + valeur exactement à l'origine → ambigu (Unknown).
    [Fact]
    public async Task ClassifyDriftAsync_NoBuildChange_ValueIsOriginal_ReturnsUnknown()
    {
        var h = new ReconcilerHarness(StateWithItem(lastBuild: "22631.3447"));
        h.Policy.Map("reflex.lowlatency", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";
        h.Probe.Set("reflex.lowlatency", "{\"v\":0}");      // == origine, pas de changement de build

        var cause = await h.Build().ClassifyDriftAsync(Reg(desired: "{\"v\":2}", original: "{\"v\":0}"));

        cause.IsOk.Should().BeTrue();
        cause.Value.Should().Be(DriftCause.Unknown);
    }

    // Valeur == voulu → pas de dérive ; rend une cause neutre, sans effet de bord.
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

    // Échec de la sonde → échec propre, ni supposition ni écriture.
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
