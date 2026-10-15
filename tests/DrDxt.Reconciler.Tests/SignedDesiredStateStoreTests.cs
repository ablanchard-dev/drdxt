using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Le magasin de manifeste DesiredState scellé et signé.
//
// Propriété de sécurité testée :
//   * Intégrité = signature asymétrique ; le vérificateur ne tient que la clé publique.
//   * Un manifeste altéré → LoadAsync échoue proprement → le réconciliateur refuse
//     d'appliquer quoi que ce soit (fermé, jamais ouvert).
//   * Défense en profondeur = une ACL ProgramData inscriptible par les seuls admins (couture de stockage).
//   * Le manifeste ne porte que des leviers réconciliables côté OS (firmware rejeté).
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

    // L'aller-retour préserve chaque champ.
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

    // Le fichier persisté vit sous une racine inscriptible par les seuls admins (la couture de
    // stockage est la frontière ACL ; le générateur de chemin de production est vérifié dans StorageTests).
    [Fact]
    public async Task PersistedFile_GoesThroughAdminWriteStorageSeam()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        storage.WriteCount.Should().Be(1);
        storage.Slot.Should().NotBeNull("the manifest is persisted via the admin-write storage seam");
    }

    // Altérer un octet du contenu canonique → la vérification échoue →
    // le chargement échoue proprement et ne rend aucun DesiredState (fermé).
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task TamperedFile_LoadFails_AndReturnsNothing()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Corrompre un octet du contenu canonique signé (l'attaquant édite le fichier).
        var original = storage.Slot!;
        var bytes = original.CanonicalJson.ToCharArray();
        bytes[bytes.Length / 2] = bytes[bytes.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = original with { CanonicalJson = new string(bytes) };

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        loaded.Value.Should().BeNull();
    }

    // Une enveloppe aux champs nuls (fichier « {} » ou ecriture interrompue) faisait lever
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

    // Une signature forgée (par une autre clé) est rejetée : le vérificateur ne tient que
    // notre clé publique, donc un manifeste forgé par le propriétaire se ferme.
    [Fact]
    public async Task ForeignSignature_LoadFails_FailClosed()
    {
        var (store, storage, _) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Re-signer le même contenu avec une paire de clés sans rapport (le « faussaire »).
        var forger = new FakeKeyPair();
        var canonical = storage.Slot!.CanonicalJson;
        var forgedSig = forger.Signer().Sign(System.Text.Encoding.UTF8.GetBytes(canonical)).Value!;
        storage.Slot = storage.Slot! with { SignatureBase64 = Convert.ToBase64String(forgedSig) };

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        loaded.Value.Should().BeNull();
    }

    // Pas de fichier = état vide propre (première installation), pas un échec.
    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsEmptyDesiredState()
    {
        var (store, _, _) = Build();
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().BeEmpty();
    }

    // Une version de schéma inconnue ou future échoue proprement (rien de deviné).
    [Fact]
    public async Task Load_UnknownSchemaVersion_FailsClean()
    {
        var (store, storage, keys) = Build();
        (await store.CommitAsync(ThreeTweaks())).IsOk.Should().BeTrue();

        // Fabriquer à la main une enveloppe correctement signée dont le corps porte un schéma futur.
        var futureBody = SignedDesiredStateStore.CanonicalizeForTest(
            new DesiredStateManifest(999, ThreeTweaks(), At));
        var sig = keys.Signer().Sign(System.Text.Encoding.UTF8.GetBytes(futureBody)).Value!;
        storage.Slot = new SignedManifestEnvelope(futureBody, Convert.ToBase64String(sig));

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ManifestSchema);
        loaded.Value.Should().BeNull();
    }

    // Un échec d'écriture laisse intact le manifeste valide précédent (atomique).
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

        // L'ancien manifeste valide doit toujours être là, intact.
        storage.Slot.Should().Be(afterFirst);
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("reflex.lowlatency");
    }

    // Un levier persistant en firmware est refusé à l'enregistrement (portée de durabilité).
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

    // Un tweakId inconnu (sans surface) est aussi refusé à l'enregistrement (prudence).
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
