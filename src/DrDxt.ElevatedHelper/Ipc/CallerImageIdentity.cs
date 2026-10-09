using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Caller image identity — resolves a connecting peer's process image to a
// (path, content-hash) pair for the hardened caller-auth (§9.13, master plan
// §9.14 "Authenticode appelant").
//
// DEV-BUILD identity (this layer): path + SHA-256 of the image file. This closes
// the gap where path equality alone would trust a binary SWAPPED at the same
// path. It is the UNSIGNED dev-build gate.
//
// Known limit, until the App is code-signed: replace the content-hash equality with real Authenticode
// verification (WinVerifyTrust WINTRUST_ACTION_GENERIC_VERIFY_V2, then extract +
// pin OUR publisher cert per 07-SECURITY §2.4/§3.2). Until the App is code-signed,
// the image-hash IS the identity proof — and it never fails open (any doubt =>
// deny). The seam shape does not change when WinVerifyTrust lands: the verifier
// just returns identity-or-null from a stronger check.
// -----------------------------------------------------------------------

/// <summary>A connecting peer's resolved image: its full path and the SHA-256 of
/// the file's bytes (null if the file could not be hashed → identity unprovable).</summary>
public readonly record struct CallerImage(string Path, byte[]? Sha256);

/// <summary>Seam: maps a client PID to its <see cref="CallerImage"/> (or null if
/// the process/path/file cannot be resolved). Tests inject a fake.</summary>
public interface ICallerImageIdentity
{
    CallerImage? Resolve(int pid);
}

/// <summary>Real resolver: PID → main-module path → SHA-256 of the file bytes.
/// Any failure (process gone, access denied, unreadable file) surfaces as null
/// (path) or a null hash, both of which the authenticator treats as deny.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32CallerImageIdentity : ICallerImageIdentity
{
    public CallerImage? Resolve(int pid)
    {
        string? path;
        try
        {
            using var proc = Process.GetProcessById(pid);
            path = proc.MainModule?.FileName;
        }
        catch
        {
            return null; // process exited / access denied → unresolved
        }

        if (string.IsNullOrWhiteSpace(path))
            return null;

        byte[]? hash = TryHashFile(path);
        return new CallerImage(path, hash);
    }

    /// <summary>SHA-256 of the file bytes, or null if it cannot be read.</summary>
    public static byte[]? TryHashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream);
        }
        catch
        {
            return null; // unreadable → caller treats as identity-unprovable → deny
        }
    }
}
