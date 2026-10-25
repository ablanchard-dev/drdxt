using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Après un passage de réconciliation, l'état annexe d'exécution est mis à jour (tenue
// de livre pour le passage suivant et l'interface) : LastReconciledAt par élément (depuis
// l'horloge injectée), LastResultCode, et le build/pilote vus maintenant. Ce n'est pas le
// manifeste, c'est l'annexe mutable. Le passage se date sur IClock, jamais UtcNow.
// -----------------------------------------------------------------------
public sealed class ReconcileStateUpdateTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReconcilePass_PersistsSidecarBookkeeping()
    {
        var h = new ReconcilerHarness(ReconcilerState.Default with
        {
            Items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") },
        });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000";
        h.Probe.Set("a", "0"); // dérivé vers l'origine → réappliqué
        h.Clock.UtcNow = new DateTimeOffset(2026, 6, 23, 14, 30, 0, TimeSpan.Zero);

        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });
        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        var saved = h.StateStore.Saved!;
        var item = saved.Items.Single(i => i.TweakId == "a");
        item.LastReconciledAt.Should().Be(h.Clock.UtcNow, "the pass timestamps against the injected clock");
        item.LastSeenBuild.Should().Be("22635.5000", "the build seen this pass is recorded");
        item.LastResultCode.Should().Be(ItemOutcome.Reapplied.ToString());
    }

    // Le helper pose la référence d'un réglage neuf pendant que la passe tourne. La passe réécrivait l'état lu à son début, et
    // cette référence disparaissait : la comptabilité se pose sur l'état relu au moment d'écrire.
    [Fact]
    public async Task Une_reference_posee_pendant_la_passe_reste()
    {
        var h = new ReconcilerHarness(ReconcilerState.Default with
        {
            Items = new[] { new ReconcilerItemState("a", false, "22631.3447", "", At, "OK") },
        });
        var neuf = new ReconcilerItemState("b", false, "22631.3447", "560.00", default, "");
        h.StoreOverride = new LeHelperEcritApresLaLecture(h.StateStore, neuf);
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.Probe.Set("a", "1");

        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });
        (await h.Build().ReconcileAsync(desired, default)).IsOk.Should().BeTrue();

        h.StateStore.Saved!.Items.Select(i => i.TweakId).Should().BeEquivalentTo(new[] { "a", "b" });
        h.StateStore.Saved.Items.Single(i => i.TweakId == "a").LastReconciledAt.Should().Be(h.Clock.UtcNow);
    }

    private sealed class LeHelperEcritApresLaLecture(InMemoryReconcilerStateStore interne, ReconcilerItemState ajout) : IReconcilerStateStore
    {
        private bool _fait;

        public async Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct)
        {
            var lu = await interne.LoadAsync(ct);
            if (!_fait)
            {
                _fait = true;
                await interne.SaveAsync(lu.Value! with { Items = [.. lu.Value!.Items, ajout] }, ct);
            }
            return lu;
        }

        public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct) => interne.SaveAsync(state, ct);
    }

    // -------------------------------------------------------------------
    // Le signal pilote, prouve sans qu'aucune main ne le pose — 11/09/2026.
    //
    // T8b/T8c prouvent que `Classify` rend `WindowsReset` quand `LastSeenDriverVersion` differe
    // de la version courante. Ils POSAIENT cette valeur eux-memes dans l'etat initial. Le produit,
    // lui, ne l'ecrivait jamais : le champ naissait vide, la branche pilote etait gardee par
    // `!string.IsNullOrEmpty(...)`, et une mise a jour de pilote qui remet un reglage a zero etait
    // NOTIFIEE au lieu d'etre reappliquee. Deux tests verts, une garantie absente.
    //
    // Ce test part d'un sidecar vide et laisse le produit l'ecrire tout seul : c'est la premiere
    // passe qui doit enregistrer la version du pilote, sans quoi la seconde ne peut rien conclure.
    // Le build est FIXE aux deux passes : seul le pilote bouge, donc seul le signal pilote peut
    // expliquer la reapplication.
    // -------------------------------------------------------------------
    [Fact]
    public async Task DeuxPasses_LePiloteChangeEntreLesDeux_LeReglageEstReapplique()
    {
        var h = new ReconcilerHarness();                      // sidecar vide : rien n'est pose a la main
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";                    // le build ne bougera pas
        h.DriverProbe.Set("NVIDIA", "555.99");
        h.Probe.Set("gpu.drs", "1");                          // deja a la valeur voulue
        var desired = new DesiredState(new[] { new AppliedTweak("gpu.drs", "1", "0", At) });
        var reconciliateur = h.Build();

        // --- passe 1 : rien a faire, mais le sidecar doit retenir le pilote vu ---
        (await reconciliateur.ReconcileAsync(desired, default)).IsOk.Should().BeTrue();
        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("555.99",
                "sans ce jeton ecrit par le produit lui-meme, le signal pilote ne peut jamais se declencher");

        // --- le pilote est mis a jour, et il a remis le reglage a son etat d'origine ---
        h.DriverProbe.Set("NVIDIA", "560.00");
        h.Probe.Set("gpu.drs", "0");

        // --- passe 2 : cause non-utilisateur → on REAPPLIQUE, on ne se contente pas de notifier ---
        var (resultat, rapport) = await reconciliateur.ReconcileWithReportAsync(desired, default);

        resultat.IsOk.Should().BeTrue();
        var item = rapport.Items.Single(i => i.TweakId == "gpu.drs");
        item.Cause.Should().Be(DriftCause.WindowsReset, "le vendeur a pousse un pilote depuis la passe precedente");
        item.Outcome.Should().Be(ItemOutcome.Reapplied);
        h.Notifier.Notified.Should().BeEmpty("une derive causee par le pilote n'est pas un choix de l'utilisateur");
        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("560.00", "le jeton suit le pilote, sinon le signal ne se rearme pas");
    }

    // Une sonde pilote muette ne doit pas EFFACER le jeton precedent : l'effacer desarmerait le
    // signal a la premiere passe ou WMI ne repond pas, exactement comme pour le build.
    [Fact]
    public async Task LectureDuPiloteEnEchec_GardeLeJetonPrecedent()
    {
        var h = new ReconcilerHarness(ReconcilerState.Default with
        {
            Items = new[] { new ReconcilerItemState("gpu.drs", false, "22631.3447", "555.99", At, "OK") },
        });
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";
        h.DriverProbe.Fail = true;                            // WMI muet cette passe
        h.Probe.Set("gpu.drs", "1");

        var desired = new DesiredState(new[] { new AppliedTweak("gpu.drs", "1", "0", At) });
        await h.Build().ReconcileAsync(desired, default);

        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("555.99");
    }

    // -------------------------------------------------------------------
    // Le trou de la toute premiere passe.
    //
    // Le test a deux passes ci-dessus prouve le signal a partir de la SECONDE passe. Sur un item
    // que le reconciliateur n'a jamais vu, `item is null`, les deux gardes de `Classify`
    // (`LastSeenBuild` et `LastSeenDriverVersion` non vides) seraient mortes : la premiere passe
    // ne pourrait que NOTIFIER, puis enregistrer l'etat deja change comme reference.
    //
    // La fenetre est reelle : l'App commite pendant la session de bureau, le service
    // reconcilie au demarrage et une fois par jour. Un pilote mis a jour entre les deux
    // tomberait exactement dedans.
    //
    // Ici, rien n'est pose a la main : c'est le produit (`ApplyReferenceSeedingStore`, le
    // chemin de commit reel) qui ecrit la reference au moment de l'apply, et la toute
    // premiere passe qui doit la lire. Le build est FIXE de bout en bout : seul le pilote
    // bouge, donc seul le signal pilote peut expliquer la reapplication.
    // -------------------------------------------------------------------
    [Fact]
    public async Task PremierePasse_LePiloteAChangeDepuisLApply_LeReglageEstReapplique()
    {
        var h = new ReconcilerHarness();                      // sidecar vide
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";                    // le build ne bougera pas
        h.DriverProbe.Set("NVIDIA", "555.99");                // le pilote au moment de l'apply
        h.Probe.Set("gpu.drs", "1");
        var desired = new DesiredState(new[] { new AppliedTweak("gpu.drs", "1", "0", At) });

        // --- l'apply commite le manifeste : le produit pose la reference tout seul ---
        var store = new ApplyReferenceSeedingStore(
            new StoreManifesteEnMemoire(), h.StateStore, h.BuildProbe, h.DriverProbe, h.Policy);
        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();
        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("555.99",
                "sans reference posee a l'apply, la 1re passe n'a rien a quoi comparer");

        // --- service arrete : le pilote est mis a jour et remet le reglage a l'origine ---
        h.DriverProbe.Set("NVIDIA", "560.00");
        h.Probe.Set("gpu.drs", "0");

        // --- La toute premiere passe du reconciliateur, pas la deuxieme ---
        var (resultat, rapport) = await h.Build().ReconcileWithReportAsync(desired, default);

        resultat.IsOk.Should().BeTrue();
        var item = rapport.Items.Single(i => i.TweakId == "gpu.drs");
        item.Cause.Should().Be(DriftCause.WindowsReset,
            "le pilote a change depuis la reference posee a l'apply");
        item.Outcome.Should().Be(ItemOutcome.Reapplied,
            "la 1re passe doit DETECTER et REAPPLIQUER, pas seulement enregistrer");
        h.Notifier.Notified.Should().BeEmpty();
        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("560.00", "le jeton se rearme sur le pilote courant");
    }

    // Le rafraichissement periodique de l'habilitation re-commite le meme etat arme. S'il
    // re-stampait la reference, il effacerait le signal a chaque tour : la derive serait
    // toujours « vue pour la premiere fois ». Une reference deja posee ne bouge pas.
    [Fact]
    public async Task UnSecondCommit_NeReecritPasUneReferenceDejaPosee()
    {
        var h = new ReconcilerHarness();
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.BuildProbe.Build = "22631.3447";
        h.DriverProbe.Set("NVIDIA", "555.99");
        var desired = new DesiredState(new[] { new AppliedTweak("gpu.drs", "1", "0", At) });
        var store = new ApplyReferenceSeedingStore(
            new StoreManifesteEnMemoire(), h.StateStore, h.BuildProbe, h.DriverProbe, h.Policy);

        await store.CommitAsync(desired);
        h.DriverProbe.Set("NVIDIA", "560.00");                // le pilote change entre les deux
        await store.CommitAsync(desired);                     // re-commit periodique

        h.StateStore.Saved!.Items.Single(i => i.TweakId == "gpu.drs")
            .LastSeenDriverVersion.Should().Be("555.99",
                "re-stamper la reference a chaque re-commit desarmerait le signal");
    }

    // Un commit qui ECHOUE n'arme rien : il ne doit donc rien referencer non plus.
    [Fact]
    public async Task UnCommitEnEchec_NePoseAucuneReference()
    {
        var h = new ReconcilerHarness();
        h.Policy.Map("gpu.drs", ReconcileSurface.Registry).Vendor("gpu.drs", "NVIDIA");
        h.DriverProbe.Set("NVIDIA", "555.99");
        var store = new ApplyReferenceSeedingStore(
            new StoreManifesteEnMemoire { Echoue = true }, h.StateStore, h.BuildProbe, h.DriverProbe, h.Policy);

        (await store.CommitAsync(new DesiredState(new[] { new AppliedTweak("gpu.drs", "1", "0", At) })))
            .IsOk.Should().BeFalse();
        h.StateStore.Saved.Should().BeNull("rien n'est arme ⇒ rien a referencer");
    }

    /// <summary>Le manifeste signe n'est pas le sujet ici : seul compte ce que le
    /// decorateur ecrit dans le sidecar apres un commit reussi.</summary>
    private sealed class StoreManifesteEnMemoire : IDesiredStateStore
    {
        private DesiredState _state = new(Array.Empty<AppliedTweak>());
        public bool Echoue { get; init; }

        public Task<Result> CommitAsync(DesiredState state)
        {
            if (Echoue) return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "commit failed (test)"));
            _state = state;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<DesiredState>> LoadAsync() => Task.FromResult(Result<DesiredState>.Ok(_state));
    }
}
