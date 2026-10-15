using System.Collections.Concurrent;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Doublures en mémoire du réconciliateur. Aucune ne touche le vrai système de fichiers,
// le registre ou la carte graphique, ni n'élève : chaque effet privilégié est modélisé
// en mémoire pour que `dotnet test` soit hermétique.
// -----------------------------------------------------------------------

/// <summary>IManifestStorage en mémoire. Modélise l'enveloppe persistée comme un seul
/// emplacement en mémoire ; les écritures sont atomiques par construction (échange d'emplacement).
/// Peut être réglé pour rater une écriture (chemin atomique / échec d'E/S).</summary>
public sealed class InMemoryManifestStorage : IManifestStorage
{
    private SignedManifestEnvelope? _slot;
    public bool FailWrite { get; set; }
    public int WriteCount { get; private set; }

    /// <summary>Accès direct pour les tests d'altération : corrompre un octet sans passer
    /// par WriteAsync (simule un attaquant qui édite le fichier au repos).</summary>
    public SignedManifestEnvelope? Slot
    {
        get => _slot;
        set => _slot = value;
    }

    public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
    {
        if (FailWrite)
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "write failed (test)"));
        // Atomique : l'emplacement valide précédent n'est remplacé qu'une fois le nouveau prêt.
        _slot = envelope;
        WriteCount++;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
        => Task.FromResult(Result<SignedManifestEnvelope?>.Ok(_slot));
}

/// <summary>Schéma asymétrique en mémoire : une paire de clés est un GUID aléatoire « secret » ;
/// le vérificateur public partage le même GUID. La signature est à la façon d'un HMAC sur les
/// octets, à clé le secret. Doublure de test seulement : elle modélise le contrat asymétrique
/// (le vérificateur vérifie, ne signe jamais) sans la dépendance native libsodium.
/// La vraie garantie asymétrique est exercée par Ed25519SignatureSchemeTests.</summary>
public sealed class FakeKeyPair
{
    public Guid Secret { get; } = Guid.NewGuid();

    public IManifestSigner Signer() => new FakeSigner(Secret);
    public IManifestVerifier Verifier() => new FakeVerifier(Secret);

    private static byte[] Mac(Guid secret, ReadOnlySpan<byte> bytes)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(secret.ToByteArray());
        return h.ComputeHash(bytes.ToArray());
    }

    private sealed class FakeSigner : IManifestSigner
    {
        private readonly Guid _secret;
        public FakeSigner(Guid secret) => _secret = secret;
        public Result<byte[]> Sign(ReadOnlySpan<byte> canonicalBytes)
            => Result<byte[]>.Ok(Mac(_secret, canonicalBytes));
    }

    private sealed class FakeVerifier : IManifestVerifier
    {
        private readonly Guid _secret;
        public FakeVerifier(Guid secret) => _secret = secret;
        public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
        {
            var expected = Mac(_secret, canonicalBytes);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, signature);
        }
    }
}

/// <summary>Politique de réconciliabilité adossée à une table explicite en mémoire : exactement
/// la forme de la table fermée de production, mais configurable par test.</summary>
public sealed class FakePolicy : IReconcilabilityPolicy
{
    private readonly Dictionary<string, ReconcileSurface> _surfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _vendors = new(StringComparer.Ordinal);

    public FakePolicy Map(string tweakId, ReconcileSurface surface)
    {
        _surfaces[tweakId] = surface;
        return this;
    }

    public FakePolicy Protect(string tweakId)
    {
        _protected.Add(tweakId);
        return this;
    }

    public FakePolicy Vendor(string tweakId, string vendor)
    {
        _vendors[tweakId] = vendor;
        return this;
    }

    public ReconcileSurface? SurfaceFor(string tweakId)
        => _surfaces.TryGetValue(tweakId, out var s) ? s : null;

    public bool IsProtected(string tweakId) => _protected.Contains(tweakId);

    public string? VendorFor(string tweakId)
        => _vendors.TryGetValue(tweakId, out var v) ? v : null;
}

/// <summary>Enregistre chaque changement journalisé via IDiagnosticsLog pour les assertions.</summary>
public sealed class RecordingDiagnostics : IDiagnosticsLog
{
    public List<(string tweakId, string from, string to, string actor)> Entries { get; } = new();
    public void Change(string tweakId, string from, string to, string actor)
        => Entries.Add((tweakId, from, to, actor));
    public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
}

/// <summary>Horloge UTC déterministe et avançable (miroir de celle d'ElevatedHelper).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>ISettingProbe sur un dictionnaire en mémoire tweakId → valeur courante.
/// Un tweakId peut être marqué pour rater sa lecture (chemin d'échec propre).</summary>
public sealed class FakeSettingProbe : ISettingProbe
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    public FakeSettingProbe Set(string tweakId, string value)
    {
        _values[tweakId] = value;
        return this;
    }

    public FakeSettingProbe FailRead(string tweakId)
    {
        _failing.Add(tweakId);
        return this;
    }

    public bool CanProbe(string tweakId) => true;

    public Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct)
    {
        if (_failing.Contains(tweakId))
            return Task.FromResult(Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, "probe failed (test)"));
        return Task.FromResult(
            _values.TryGetValue(tweakId, out var v)
                ? Result<string>.Ok(v)
                : Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, "no value (test)"));
    }
}

/// <summary>IElevatedReapplyClient qui écrit la valeur voulue dans une FakeSettingProbe
/// donnée (pour qu'une relecture de confirmation la voie) et enregistre chaque
/// réapplication. Peut refuser un tweakId choisi, ou écrire une mauvaise valeur
/// (chemin de confirmation après écriture).</summary>
public sealed class FakeReapplyClient : IElevatedReapplyClient
{
    private readonly FakeSettingProbe _probe;
    private readonly Func<string, bool>? _refuse;
    private readonly Func<string, bool>? _writeWrong;

    public List<(string tweakId, string value)> Reapplies { get; } = new();

    public FakeReapplyClient(
        FakeSettingProbe probe,
        Func<string, bool>? refuse = null,
        Func<string, bool>? writeWrong = null)
    {
        _probe = probe;
        _refuse = refuse;
        _writeWrong = writeWrong;
    }

    public Task<Result> ReapplyAsync(string tweakId, string desiredValueJson, CancellationToken ct)
    {
        if (_refuse is not null && _refuse(tweakId))
            return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refused (test)"));

        Reapplies.Add((tweakId, desiredValueJson));

        // Modéliser l'écriture privilégiée qui atterrit dans l'état du système.
        var landed = _writeWrong is not null && _writeWrong(tweakId)
            ? desiredValueJson + "_WRONG"
            : desiredValueJson;
        _probe.Set(tweakId, landed);
        return Task.FromResult(Result.Ok());
    }
}

/// <summary>Notificateur qui enregistre chaque notification UserIntentional.</summary>
public sealed class RecordingNotifier : IReconcilerNotifier
{
    public List<(string tweakId, string current)> Notified { get; } = new();
    public Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct)
    {
        Notified.Add((tweak.TweakId, currentValueJson));
        return Task.CompletedTask;
    }
}

/// <summary>Magasin d'état annexe en mémoire. <see cref="FailLoad"/> / <see cref="FailSave"/>
/// forcent indépendamment l'op correspondante à échouer (chemins d'échec propre) ;
/// <see cref="SaveCount"/> prouve qu'un Load raté n'a jamais atteint Save.</summary>
public sealed class InMemoryReconcilerStateStore : IReconcilerStateStore
{
    private ReconcilerState? _state;
    public ReconcilerState? Saved => _state;
    public bool FailLoad { get; set; }
    public bool FailSave { get; set; }
    public int SaveCount { get; private set; }

    public InMemoryReconcilerStateStore(ReconcilerState? initial = null) => _state = initial;

    public Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct)
        => Task.FromResult(FailLoad
            ? Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, "load failed (test)")
            : Result<ReconcilerState>.Ok(_state ?? ReconcilerState.Default));

    public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct)
    {
        if (FailSave)
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "save failed (test)"));
        _state = state;
        SaveCount++;
        return Task.FromResult(Result.Ok());
    }
}

/// <summary>Fausse sonde de build de l'OS.</summary>
public sealed class FakeOsBuildProbe : IOsBuildProbe
{
    public string? Build { get; set; }
    public bool Fail { get; set; }
    public Result<string> ReadCurrentBuild()
        => Fail
            ? Result<string>.Fail(ErrorCodes.RegistryFail, "build read failed (test)")
            : Result<string>.Ok(Build ?? "22631.3447");
}

/// <summary>Fausse sonde de pilote GPU par constructeur.</summary>
public sealed class FakeGpuDriverProbe : IGpuDriverProbe
{
    private readonly Dictionary<string, string?> _byVendor = new(StringComparer.OrdinalIgnoreCase);
    public bool Fail { get; set; }

    public FakeGpuDriverProbe Set(string vendor, string? version)
    {
        _byVendor[vendor] = version;
        return this;
    }

    public Result<string?> ReadDriverVersion(string vendor)
    {
        if (Fail) return Result<string?>.Fail(ErrorCodes.WmiFail, "driver read failed (test)");
        return Result<string?>.Ok(_byVendor.TryGetValue(vendor, out var v) ? v : null);
    }
}

/// <summary>Faux magasin d'issue de démarrage : lit une série fixe ou mutable, et les
/// méthodes d'écriture mutent cette série en mémoire (incrément au démarrage, remise à zéro
/// à la session stable). Le bouton <see cref="Fail"/> fait échouer chaque opération.</summary>
public sealed class FakeBootOutcomeStore : IBootOutcomeStore
{
    public int Streak { get; set; }
    public bool Fail { get; set; }

    public Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct)
        => Task.FromResult(Fail
            ? Result<int>.Fail(ErrorCodes.IoFailure, "boot store failed (test)")
            : Result<int>.Ok(Streak));

    public Task<Result> MarkBootStartedAsync(CancellationToken ct)
    {
        if (Fail) return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "boot store failed (test)"));
        Streak++;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> MarkSessionStableAsync(CancellationToken ct)
    {
        if (Fail) return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "boot store failed (test)"));
        Streak = 0;
        return Task.FromResult(Result.Ok());
    }
}
