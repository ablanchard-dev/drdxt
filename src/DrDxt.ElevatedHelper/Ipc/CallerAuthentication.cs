using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Caller authorization seam.
//
// SECURITY (master plan §9.14: "jamais d'auth stubée en build distribuable"):
//   * Authorization is a SEPARATE concern from transport integrity (HMAC).
//     HMAC proves "the bytes are intact and the peer holds the session key";
//     this seam decides "is the connecting peer ENTITLED to run privileged
//     ops at all" — i.e. is it really our DrDxt.App.
//   * The production/default implementation denies by default. It must never
//     silently allow-all in a shippable build. Tests inject an allowing fake.
// -----------------------------------------------------------------------

/// <summary>Caller context for an authorization decision. Carries the connecting
/// peer's process id (resolved from the pipe via GetNamedPipeClientProcessId) so
/// the authenticator can verify the peer is our DrDxt.App. A null pid means the
/// pid could not be resolved → deny.</summary>
public readonly record struct CallerInfo(int? ClientProcessId = null);

/// <summary>Fail-closed authorization decision for a connecting peer.</summary>
public interface ICallerAuthenticator
{
    Result Authorize(CallerInfo caller);
}

/// <summary>
/// DEFAULT fail-closed authenticator. DENIES every caller. Used as a safe
/// fallback / in tests of the deny path; the App-process authenticator
/// (DrDxtAppCallerAuthenticator) is what a real elevated host wires.
/// </summary>
public sealed class DenyByDefaultCallerAuthenticator : ICallerAuthenticator
{
    public Result Authorize(CallerInfo caller) =>
        Result.Fail(ErrorCodes.ElevationDenied, "Caller authorization denied (deny-by-default).");
}

/// <summary>
/// Authorizes a peer that matches ANY ONE of a SET of trusted caller images — the
/// helper must accept MULTIPLE distinct callers (the interactive DrDxt.App AND the
/// low-priv reconciler host), each its own path+SHA-256 authenticator.
///
/// SECURITY (M5 multi-caller channel): the peer is exactly one process, so an OR over
/// the per-image authenticators is correct — it authorizes iff the peer IS one of the
/// trusted images. It is fail-closed by construction:
///   * an EMPTY set authorizes nothing (deny-by-default — no trusted image provisioned);
///   * a peer that matches none of the members → DENY (third-party, or a swapped binary
///     at a trusted path: each member also enforces SHA-256, so the path alone never wins);
///   * a member that THROWS is treated as "did not authorize" (defense in depth) — one
///     faulty member can never crash the decision, and never silently allow.
/// </summary>
public sealed class CompositeCallerAuthenticator : ICallerAuthenticator
{
    private readonly ICallerAuthenticator[] _members;

    public CompositeCallerAuthenticator(params ICallerAuthenticator[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        // Defensive copy; reject null entries so a null can never short-circuit/throw.
        _members = members.ToArray();
        if (Array.IndexOf(_members, null) >= 0)
            throw new ArgumentNullException(nameof(members), "Authenticator members must be non-null.");
    }

    public CompositeCallerAuthenticator(IEnumerable<ICallerAuthenticator> members)
        : this((members ?? throw new ArgumentNullException(nameof(members))).ToArray()) { }

    public Result Authorize(CallerInfo caller)
    {
        foreach (var member in _members)
        {
            Result decision;
            try
            {
                decision = member.Authorize(caller);
            }
            catch
            {
                // A throwing member never allows and never crashes the decision.
                continue;
            }

            if (decision.IsOk)
                return Result.Ok();
        }

        // No member authorized the peer (or there were none) → deny, fail-closed.
        return Result.Fail(ErrorCodes.ElevationDenied,
            "Caller authorization denied (not one of the trusted caller images).");
    }
}

/// <summary>
/// Authorizes a connecting peer only when its process image is the trusted
/// DrDxt.App executable.
///
/// SECURITY: the pipe is already ACL-restricted to the current user, so the peer
/// runs as the same user. This adds a process-IDENTITY check on top:
///   * resolve the client's PID (from the pipe) → its main-module image path,
///     and require it to equal the expected DrDxt.App path (full, case-insensitive);
///   * (HARDENED, M5) also require the image's SHA-256 to match the expected
///     App's — so a DIFFERENT binary sitting at the same path is rejected. This
///     is the UNSIGNED dev-build identity proof. Use <see cref="WithImageIdentity"/>.
/// Anything uncertain — no PID, unresolved path/hash, mismatch, or a throwing
/// resolver — DENIES (fail-closed; never fail-open).
///
/// Known limit, until the App is code-signed: replace the image-hash equality with full Authenticode
/// verification of the caller image (WinVerifyTrust + pin OUR publisher cert,
/// 07-SECURITY §2.4/§3.2) once DrDxt.App is code-signed. The image-hash is the
/// dev-grade stand-in until then; the seam shape does not change when it lands.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DrDxtAppCallerAuthenticator : ICallerAuthenticator
{
    private readonly string _expectedImagePath;
    private readonly byte[]? _expectedImageHash;        // null → path-only (legacy dev-grade)
    private readonly Func<int, string?> _resolveImagePath;
    private readonly ICallerImageIdentity? _imageIdentity; // non-null → hardened path

    /// <summary>Path-only authenticator (legacy R1 dev-grade). Kept for callers/tests
    /// that only assert the image path. Prefer <see cref="WithImageIdentity"/>.</summary>
    /// <param name="expectedImagePath">Full path of the trusted DrDxt.App exe.</param>
    /// <param name="resolveImagePath">Maps a client PID → its main-module image
    /// path. Defaults to the real Win32 resolver; tests inject a fake.</param>
    public DrDxtAppCallerAuthenticator(string expectedImagePath, Func<int, string?>? resolveImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath))
            throw new ArgumentException("Expected image path must be non-empty.", nameof(expectedImagePath));
        _expectedImagePath = Path.GetFullPath(expectedImagePath);
        _resolveImagePath = resolveImagePath ?? DefaultResolveImagePath;
        _expectedImageHash = null;
        _imageIdentity = null;
    }

    private DrDxtAppCallerAuthenticator(
        string expectedImagePath, byte[] expectedImageHash, ICallerImageIdentity imageIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath))
            throw new ArgumentException("Expected image path must be non-empty.", nameof(expectedImagePath));
        if (expectedImageHash is null || expectedImageHash.Length == 0)
            throw new ArgumentException("Expected image hash must be non-empty.", nameof(expectedImageHash));
        _expectedImagePath = Path.GetFullPath(expectedImagePath);
        _expectedImageHash = (byte[])expectedImageHash.Clone();
        _imageIdentity = imageIdentity ?? throw new ArgumentNullException(nameof(imageIdentity));
        _resolveImagePath = DefaultResolveImagePath; // unused on the hardened path
    }

    /// <summary>HARDENED authenticator: requires the peer's image path AND its
    /// SHA-256 to match the expected App. The dev-build identity gate (closes the
    /// swapped-exe-at-same-path gap). <paramref name="imageIdentity"/> defaults to
    /// the real Win32 path+hash resolver.</summary>
    public static DrDxtAppCallerAuthenticator WithImageIdentity(
        string expectedImagePath, byte[] expectedImageHash, ICallerImageIdentity? imageIdentity = null) =>
        new(expectedImagePath, expectedImageHash, imageIdentity ?? new Win32CallerImageIdentity());

    public Result Authorize(CallerInfo caller)
    {
        // No PID resolved → cannot identify the peer → deny.
        if (caller.ClientProcessId is not { } pid || pid <= 0)
            return Deny("Could not resolve the connecting client's process id.");

        return _imageIdentity is null
            ? AuthorizeByPath(pid)
            : AuthorizeByImageIdentity(pid);
    }

    // Legacy dev-grade: path equality only.
    private Result AuthorizeByPath(int pid)
    {
        string? actual;
        try
        {
            actual = _resolveImagePath(pid);
        }
        catch
        {
            // A throwing resolver (process exited, access denied, …) → deny.
            return Deny("Could not resolve the connecting client's image path.");
        }

        if (string.IsNullOrWhiteSpace(actual))
            return Deny("Connecting client has no resolvable image path.");

        string normalized;
        try
        {
            normalized = Path.GetFullPath(actual);
        }
        catch
        {
            return Deny("Connecting client image path is malformed.");
        }

        return PathMatches(normalized)
            ? Result.Ok()
            : Deny("Connecting client is not the trusted DrDxt.App process.");
    }

    // Hardened: path equality AND image-hash equality (constant-time).
    private Result AuthorizeByImageIdentity(int pid)
    {
        CallerImage? resolved;
        try
        {
            resolved = _imageIdentity!.Resolve(pid);
        }
        catch
        {
            return Deny("Could not resolve the connecting client's image identity.");
        }

        if (resolved is not { } image)
            return Deny("Connecting client image identity is unresolved.");

        string normalized;
        try
        {
            normalized = Path.GetFullPath(image.Path);
        }
        catch
        {
            return Deny("Connecting client image path is malformed.");
        }

        if (!PathMatches(normalized))
            return Deny("Connecting client is not the trusted DrDxt.App process.");

        // No hash resolved (file unreadable) → identity unprovable → deny.
        if (image.Sha256 is null || image.Sha256.Length == 0)
            return Deny("Connecting client image could not be hashed for identity.");

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   image.Sha256, _expectedImageHash!)
            ? Result.Ok()
            : Deny("Connecting client image does not match the trusted DrDxt.App binary.");
    }

    private bool PathMatches(string normalizedActual) =>
        string.Equals(normalizedActual, _expectedImagePath, StringComparison.OrdinalIgnoreCase);

    private static string? DefaultResolveImagePath(int pid)
    {
        using var proc = System.Diagnostics.Process.GetProcessById(pid);
        return proc.MainModule?.FileName;
    }

    private static Result Deny(string message) => Result.Fail(ErrorCodes.ElevationDenied, message);
}
