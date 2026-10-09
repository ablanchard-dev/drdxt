using System.Collections.Concurrent;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// In-memory test doubles for the M4 reconciler. None of them touch the real
// filesystem, registry, GPU, or perform elevation — every privileged effect is
// modelled in memory so `dotnet test` is hermetic.
// -----------------------------------------------------------------------

/// <summary>In-memory IManifestStorage. Models the persisted envelope as a single
/// in-memory slot; writes are atomic by construction (slot swap). Can be told to
/// fail a write (to exercise the atomic / IO-failure path).</summary>
public sealed class InMemoryManifestStorage : IManifestStorage
{
    private SignedManifestEnvelope? _slot;
    public bool FailWrite { get; set; }
    public int WriteCount { get; private set; }

    /// <summary>Direct access for tamper tests — corrupt a byte without going
    /// through WriteAsync (simulates an attacker editing the file at rest).</summary>
    public SignedManifestEnvelope? Slot
    {
        get => _slot;
        set => _slot = value;
    }

    public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
    {
        if (FailWrite)
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "write failed (test)"));
        // Atomic: the previous valid slot is only replaced once the new one is ready.
        _slot = envelope;
        WriteCount++;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
        => Task.FromResult(Result<SignedManifestEnvelope?>.Ok(_slot));
}

/// <summary>An in-memory asymmetric scheme: a keypair is a random GUID "secret"; the
/// public verifier shares the same GUID. The signature is HMAC-like over the bytes
/// keyed by the secret. This is a TEST DOUBLE only — it models the asymmetric
/// contract (verifier checks, never signs) without the native libsodium dependency.
/// The real asymmetric guarantee is exercised by Ed25519SignatureSchemeTests.</summary>
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

/// <summary>A reconcilability policy backed by an explicit in-memory table — exactly
/// the shape of the production closed table, but configurable per test.</summary>
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

/// <summary>Records every change logged through IDiagnosticsLog for assertions.</summary>
public sealed class RecordingDiagnostics : IDiagnosticsLog
{
    public List<(string tweakId, string from, string to, string actor)> Entries { get; } = new();
    public void Change(string tweakId, string from, string to, string actor)
        => Entries.Add((tweakId, from, to, actor));
    public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
}

/// <summary>A deterministic, advanceable UTC clock (mirrors the ElevatedHelper one).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>An ISettingProbe over an in-memory dictionary of tweakId → current value.
/// A tweakId can be flagged to FAIL its read (to exercise the fail-clean path).</summary>
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

/// <summary>An IElevatedReapplyClient that WRITES the desired value into a given
/// FakeSettingProbe (so a confirming read-back then sees it) and records every
/// re-apply. Can be told to refuse a chosen tweakId, or to write a WRONG value
/// (to exercise the post-write confirmation path, T21).</summary>
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

        // Model the privileged write landing in the system state.
        var landed = _writeWrong is not null && _writeWrong(tweakId)
            ? desiredValueJson + "_WRONG"
            : desiredValueJson;
        _probe.Set(tweakId, landed);
        return Task.FromResult(Result.Ok());
    }
}

/// <summary>A notifier that records every UserIntentional notification.</summary>
public sealed class RecordingNotifier : IReconcilerNotifier
{
    public List<(string tweakId, string current)> Notified { get; } = new();
    public Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct)
    {
        Notified.Add((tweak.TweakId, currentValueJson));
        return Task.CompletedTask;
    }
}

/// <summary>In-memory sidecar state store. <see cref="FailLoad"/> / <see cref="FailSave"/>
/// independently force the corresponding op to Fail (to exercise fail-clean paths);
/// <see cref="SaveCount"/> proves a failed Load never reached Save.</summary>
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

/// <summary>Fake OS-build probe.</summary>
public sealed class FakeOsBuildProbe : IOsBuildProbe
{
    public string? Build { get; set; }
    public bool Fail { get; set; }
    public Result<string> ReadCurrentBuild()
        => Fail
            ? Result<string>.Fail(ErrorCodes.RegistryFail, "build read failed (test)")
            : Result<string>.Ok(Build ?? "22631.3447");
}

/// <summary>Fake per-vendor GPU driver probe.</summary>
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

/// <summary>Fake boot-outcome store: reads a fixed/mutable streak, and the write
/// methods mutate that in-memory streak (increment on boot-started, reset on
/// session-stable). The <see cref="Fail"/> knob fails every operation.</summary>
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
