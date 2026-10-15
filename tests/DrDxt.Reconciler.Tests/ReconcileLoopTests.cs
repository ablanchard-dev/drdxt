using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// ReconcileAsync, les invariants de base (et la garde anti-boucle de démarrage).
//
// Chaque test pilote la boucle avec des faux en mémoire ; le FakeReapplyClient est la
// seule chose qui « écrit » (dans la FakeSettingProbe), pour vérifier exactement
// quels éléments ont été réappliqués, notifiés ou refusés, et que les passages
// idempotents ne font rien.
// -----------------------------------------------------------------------
public sealed class ReconcileLoopTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static AppliedTweak Tweak(string id, string desired, string original)
        => new(id, desired, original, At);

    // Un banc dont l'état suit les éléments listés comme déjà appliqués au build donné, et
    // dont la sonde de build annonce un build changé (pour qu'une dérive vers l'origine
    // soit classée WindowsReset → réappliquer).
    private static ReconcilerHarness ResetScenario(params string[] ids)
    {
        var items = ids.Select(id =>
            new ReconcilerItemState(id, false, "22631.3447", "555.99", At, "OK")).ToArray();
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        foreach (var id in ids) h.Policy.Map(id, ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000"; // build changé → WindowsReset sur une valeur d'origine
        return h;
    }

    // Idempotence : tout déjà à la valeur voulue ⇒ zéro écriture, zéro Change.
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_WhenAllInDesiredState_DoesNothing()
    {
        var h = ResetScenario("a", "b");
        h.Probe.Set("a", "1").Set("b", "2");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty("no item drifted");
        h.Diagnostics.Entries.Should().BeEmpty("no Change is logged on a pure no-op pass");
    }

    // Réparer une fois, puis un second passage ne fait rien.
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_RunTwice_SecondPassIsNoOp()
    {
        var h = ResetScenario("a");
        h.Probe.Set("a", "0"); // dérivé vers l'origine
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var first = await h.Build().ReconcileAsync(desired, default);
        first.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("a");

        // Second passage : la sonde lit désormais « 1 » (le FakeReapplyClient l'a écrit).
        var second = await h.Build().ReconcileAsync(desired, default);
        second.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle("the second pass must add NO new write");
    }

    // Seul l'élément dérivé est réappliqué.
    [Fact]
    public async Task ReconcileAsync_OnlyReappliesDriftedItems()
    {
        var h = ResetScenario("a", "b", "c");
        h.Probe.Set("a", "1").Set("b", "0").Set("c", "3"); // seul b a dérivé vers l'origine
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"), Tweak("b", "2", "0"), Tweak("c", "3", "0"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("b");
        h.Diagnostics.Entries.Where(e => e.actor == "DrDXT").Should().ContainSingle();
    }

    // Chaque réapplication est journalisée avec les bons de/vers/acteur.
    [Fact]
    public async Task ReconcileAsync_LogsEachReapplication()
    {
        var h = ResetScenario("a");
        h.Probe.Set("a", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        await h.Build().ReconcileAsync(desired, default);

        h.Diagnostics.Entries.Should().Contain(e =>
            e.tweakId == "a" && e.from == "0" && e.to == "1" && e.actor == "DrDXT");
    }

    // UserIntentional ⇒ notifier, pas d'écrasement, toujours Ok.
    [Fact]
    public async Task ReconcileAsync_UserIntentionalDrift_NotifiesAndDoesNotOverwrite()
    {
        // Pas de changement de build + une troisième valeur ⇒ UserIntentional.
        var items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447"; // UNCHANGED
        h.Probe.Set("a", "9");             // ≠ voulu ≠ origine (choix du joueur)
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue("notifying is not a failure");
        h.Notifier.Notified.Should().ContainSingle().Which.tweakId.Should().Be("a");
        h.Reapply.Reapplies.Should().BeEmpty("a user choice is never overwritten");
    }

    // Unknown ⇒ prudence (notifier, pas d'écrasement par défaut).
    [Fact]
    public async Task ReconcileAsync_UnknownDrift_DoesNotOverwriteByDefault()
    {
        var items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";  // UNCHANGED
        h.Probe.Set("a", "0");              // == origine, pas de changement de build ⇒ Unknown
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty();
        h.Notifier.Notified.Should().ContainSingle();
    }

    // Un élément échoue, les autres se réconcilient quand même ; Fail agrégé.
    [Fact]
    public async Task ReconcileAsync_OneItemFails_OthersStillReconcile_ResultIsAggregatedFail()
    {
        var h = ResetScenario("a", "b", "c");
        // Le client de réapplication refuse « b » (l'op du helper échoue).
        var reapply = new FakeReapplyClient(h.Probe, refuse: id => id == "b");
        h.WithReapply(reapply);
        h.Probe.Set("a", "0").Set("b", "0").Set("c", "0"); // tous dérivés vers l'origine
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"), Tweak("b", "2", "0"), Tweak("c", "3", "0"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        result.Error.Value.Message.Should().Contain("b");
        // a et c ont réussi ; b non.
        reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    // Sécurité : un tweakId hors liste blanche est refusé, aucun IPC pour lui.
    [Fact]
    public async Task ReconcileAsync_NeverAppliesTweakNotInVerifiedManifest()
    {
        var h = ResetScenario("a");          // seul « a » est dans la politique
        h.Probe.Set("a", "0").Set("ghost", "0");
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"),
            Tweak("ghost", "1", "0"),        // inconnu / hors liste blanche
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().NotContain("ghost");
        // une ligne de journal de sécurité pour le refus.
        h.Diagnostics.Entries.Should().Contain(e => e.tweakId == "ghost" && e.actor.Contains("refused"));
    }

    // Écriture annoncée OK mais relecture toujours dérivée ⇒ élément KO.
    [Fact]
    public async Task ReconcileAsync_WriteSucceedsButRelectReadStillDrifted_MarksItemFailed()
    {
        var h = ResetScenario("a");
        var reapply = new FakeReapplyClient(h.Probe, writeWrong: id => id == "a"); // écrit une mauvaise valeur
        h.WithReapply(reapply);
        h.Probe.Set("a", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        reapply.Reapplies.Should().ContainSingle("the write was attempted");
        // La relecture de confirmation n'a pas vu la valeur voulue → KO, pas un faux succès.
    }

    // Une surface protégée (Defender/Update/BITS) est refusée net. C'est un invariant « ne pas
    // nuire » aussi porteur que l'altération ou l'idempotence, donc il passe par la même porte
    // dure de la CI (Category=ReconcileGate + TreatNoTestsAsError) : si ce test est supprimé ou
    // son [Trait] retiré, l'étape de porte trouve moins de tests et la CI échoue ; la garantie
    // ne peut pas disparaître en silence. Une mutation du refus restait verte sur la porte
    // (prouvée par un test ordinaire, mais non gardée).
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_RejectsProtectedSurfaces()
    {
        var h = ResetScenario("a");
        h.Policy.Map("svc.windefend", ReconcileSurface.Service).Protect("svc.windefend");
        h.Probe.Set("a", "0").Set("svc.windefend", "0");
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"),
            Tweak("svc.windefend", "Stopped", "Running"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().NotContain("svc.windefend");
        h.Diagnostics.Entries.Should().Contain(e =>
            e.tweakId == "svc.windefend" && e.actor.Contains("protected"));
    }

    // Coupe-circuit par élément : comparé et journalisé, jamais réappliqué.
    [Fact]
    public async Task ReconcileAsync_ItemPaused_IsComparedButNotReapplied()
    {
        var items = new[]
        {
            new ReconcilerItemState("a", true, "22631.3447", "555.99", At, "OK"),  // PAUSED
            new ReconcilerItemState("b", false, "22631.3447", "555.99", At, "OK"),
        };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry).Map("b", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000"; // build changé
        h.Probe.Set("a", "0").Set("b", "0"); // les deux dérivés vers l'origine
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "b" });
        // a a été lu pour les diagnostics (une ligne « en pause ») mais pas réappliqué.
        h.Diagnostics.Entries.Should().Contain(e => e.tweakId == "a" && e.actor.Contains("paused"));
    }

    // Coupe-circuit global : zéro écriture quelle que soit la dérive, Ok + rapport en pause.
    [Fact]
    public async Task ReconcileAsync_GlobalPaused_SkipsAllWrites()
    {
        var h = ResetScenario("a", "b");
        await h.StateStore.SaveAsync(
            (h.StateStore.Saved ?? ReconcilerState.Default) with { GlobalPaused = true }, default);
        h.Probe.Set("a", "0").Set("b", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var reconciler = h.Build();
        var (result, report) = await reconciler.ReconcileWithReportAsync(desired, default);

        result.IsOk.Should().BeTrue();
        report.GloballyPaused.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty();
    }

    // Anti-boucle de démarrage : l'élément de réglage GPU est suspendu au seuil de série,
    // les éléments sûrs continuent de se réconcilier.
    [Fact]
    public async Task ReconcileAsync_BootLoopStreakReached_SuspendsGpuReapply_ButKeepsSafeItems()
    {
        var h = ResetScenario("reg.safe", "gpu.offset");
        h.Policy.Vendor("gpu.offset", "NVIDIA"); // classe réglage GPU
        h.BootLoopThreshold = 3;
        h.BootStore.Streak = 3;                  // au seuil → suspendre la réapplication GPU
        h.Probe.Set("reg.safe", "0").Set("gpu.offset", "0"); // les deux dérivés vers l'origine
        var desired = new DesiredState(new[]
        {
            Tweak("reg.safe", "1", "0"),
            Tweak("gpu.offset", "1", "0"),
        });

        var (result, report) = await h.Build().ReconcileWithReportAsync(desired, default);

        // L'élément de registre sûr est réappliqué ; l'élément GPU est suspendu, pas en échec.
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "reg.safe" });
        report.Suspended.Select(i => i.TweakId).Should().BeEquivalentTo(new[] { "gpu.offset" });
        result.IsOk.Should().BeTrue("a suspended GPU item is a safe state, not a failure");
    }

    // L'élément GPU est bien réappliqué quand les démarrages sont sains.
    [Fact]
    public async Task ReconcileAsync_BootLoopBelowThreshold_ReappliesGpuItem()
    {
        var h = ResetScenario("gpu.offset");
        h.Policy.Vendor("gpu.offset", "NVIDIA");
        h.BootLoopThreshold = 3;
        h.BootStore.Streak = 1; // assez sain
        h.Probe.Set("gpu.offset", "0");
        var desired = new DesiredState(new[] { Tweak("gpu.offset", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "gpu.offset" });
    }
}
