using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T1-T7 — the SEALED, SIGNED DesiredState manifest store.
//
// Security property under test (master §9.12 / B1, plan §2):
//   * Integrity = ASYMMETRIC signature; the verifier holds only the PUBLIC key.
//   * A TAMPERED manifest → LoadAsync fails clean → the reconciler refuses to
//     apply anything (fail-CLOSED, never fail-open).
//   * Defence-in-depth = an admin-write-only ProgramData ACL (storage seam).
//   * The manifest carries only OS-side reconcilable tweaks (firmware rejected).
// -----------------------------------------------------------------------
public sealed class SignedDesiredStateStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static FakePolicy RegistryPolicy() => new FakePolicy()
        .Map("reflex.lowlatency", ReconcileSurface.Registry)
        .Map("power.plan", ReconcileSurface.PowerPlan)
        .Map("svc.x3d", ReconcileSurface.Service);

    private static DesiredState ThreeTweaks() => new(new[]
    {
        new AppliedTweak("reflex.lowlatency", "{\"v\":2}", "{\"v\":0}", At),
        new AppliedTweak("power.plan", "\"high\"", "\"balanced\"", At),
        new AppliedTweak("svc.x3d", "\"Running\"", "\"Stopped\"", At),
    });

    private static (SignedDesiredStateStore store, InMemoryManifestStorage storage, FakeKeyPair keys)
        Build(IReconcilabilityPolicy? policy = null)
    {
        var keys = new FakeKeyPair();
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };
        var store = new SignedDesiredStateStore(
            storage, keys.Signer(), keys.Verifier(), policy ?? RegistryPolicy(), clock);
        return (store, storage, keys);
    }

    // T1 — round-trip preserves every field.
    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var (store, _, _) = Build();
        var desired = ThreeTweaks();

        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().BeEquivalentTo(desired.Tweaks, o => o.WithStrictOrdering());
    }

    // T2 — the persisted file lives under an admin-write-only root (the storage seam
    // is the ACL boundary; production path generator is asserted in StorageTests).
    [Fact]
    public async Task PersistedFile_GoesThroughAdminWriteStorageSeam()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        storage.WriteCount.Should().Be(1);
        storage.Slot.Should().NotBeNull("the manifest is persisted via the admin-write storage seam");
    }

    // T3 — tampering with one byte of the canonical content → verification fails →
    // load fails clean and returns no DesiredState (fail-CLOSED).
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task TamperedFile_LoadFails_AndReturnsNothing()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Corrupt one byte of the signed canonical content (attacker edits the file).
        var original = storage.Slot!;
        var bytes = original.CanonicalJson.ToCharArray();
        bytes[bytes.Length / 2] = bytes[bytes.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = original with { CanonicalJson = new string(bytes) };

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        loaded.Value.Should().BeNull();
    }

    // 26/09/2026 : une enveloppe aux champs nuls (fichier « {} » ou ecriture interrompue) faisait lever
    // Convert.FromBase64String(null) : le service tombait au lieu d'echouer proprement.
    [Theory]
    [InlineData(null, "c2ln")]
    [InlineData("{}", null)]
    [InlineData(null, null)]
    public async Task Une_enveloppe_aux_champs_nuls_echoue_proprement(string? canonique, string? signature)
    {
        var (store, storage, _) = Build();
        storage.Slot = new SignedManifestEnvelope(canonique!, signature!);

        var charger = () => store.LoadAsync();

        await charger.Should().NotThrowAsync();
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }

    // T3b — a forged signature (signed by a DIFFERENT key) is rejected: the verifier
    // holds only OUR public key, so an owner-forged manifest fails-closed.
    [Fact]
    public async Task ForeignSignature_LoadFails_FailClosed()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Re-sign the same content with an UNRELATED keypair (the "forger").
        var forger = new FakeKeyPair();
        var canonical = storage.Slot!.CanonicalJson;
        var forgedSig = forger.Signer().Sign(System.Text.Encoding.UTF8.GetBytes(canonical)).Value!;
        storage.Slot = storage.Slot! with { SignatureBase64 = Convert.ToBase64String(forgedSig) };

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        loaded.Value.Should().BeNull();
    }

    // T4 — no file = clean empty state (first install), not a failure.
    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsEmptyDesiredState()
    {
        var (store, _, _) = Build();
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().BeEmpty();
    }

    // T5 — an unknown/future schema version fails clean (no guessing).
    [Fact]
    public async Task Load_UnknownSchemaVersion_FailsClean()
    {
        var (store, storage, keys) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Hand-craft a correctly-signed envelope whose body has a FUTURE schema.
        var futureBody = SignedDesiredStateStore.CanonicalizeForTest(
            new DesiredStateManifest(999, ThreeTweaks(), At));
        var sig = keys.Signer().Sign(System.Text.Encoding.UTF8.GetBytes(futureBody)).Value!;
        storage.Slot = new SignedManifestEnvelope(futureBody, Convert.ToBase64String(sig));

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ManifestSchema);
        loaded.Value.Should().BeNull();
    }

    // T6 — a write failure leaves the previous valid manifest intact (atomic).
    [Fact]
    public async Task SaveAsync_IsAtomic_NoPartialFileOnFailure()
    {
        var (store, storage, _) = Build();
        var first = new DesiredState(new[]
        {
            new AppliedTweak("reflex.lowlatency", "{\"v\":1}", "{\"v\":0}", At),
        });
        (await store.CommitAsync(first)).IsOk.Should().BeTrue();
        var afterFirst = storage.Slot;

        storage.FailWrite = true;
        var second = await store.CommitAsync(ThreeTweaks());
        second.IsOk.Should().BeFalse();
        second.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);

        // The old valid manifest must still be there, untouched.
        storage.Slot.Should().Be(afterFirst);
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("reflex.lowlatency");
    }

    // T7 — a firmware-persistent tweak is refused at save (durability scope §4 P4).
    [Fact]
    public async Task SaveAsync_RejectsFirmwarePersistentTweaks()
    {
        var policy = RegistryPolicy().Map("bios.xmp", ReconcileSurface.Firmware);
        var (store, storage, _) = Build(policy);

        var withFirmware = new DesiredState(new[]
        {
            new AppliedTweak("reflex.lowlatency", "{\"v\":2}", "{\"v\":0}", At),
            new AppliedTweak("bios.xmp", "\"on\"", "\"off\"", At),
        });

        var result = await store.CommitAsync(withFirmware);
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ManifestNonReconcilable);
        storage.WriteCount.Should().Be(0, "nothing is persisted when the manifest is invalid");
    }

    // T7b — an unknown tweakId (no surface) is also refused at save (conservative).
    [Fact]
    public async Task SaveAsync_RejectsUnknownTweak()
    {
        var (store, _, _) = Build();
        var withUnknown = new DesiredState(new[]
        {
            new AppliedTweak("totally.unknown", "{}", "{}", At),
        });

        var result = await store.CommitAsync(withUnknown);
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ManifestNonReconcilable);
    }
}
