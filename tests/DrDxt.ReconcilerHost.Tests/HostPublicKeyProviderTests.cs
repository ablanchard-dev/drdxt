using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostPublicKeyProvider — loads the verify-only PUBLIC key from a known location.
//
// The host VERIFIES manifests; it never signs (no private key, ever). This loads the
// 32-byte raw Ed25519 public key from a file under the admin-write manifest root.
//   * present + 32 bytes → Ok(key)
//   * absent              → Fail (no key provisioned yet — the host then has no
//                           verifier and refuses to reconcile, fail-closed)
//   * wrong length        → Fail (a malformed key never silently "works")
// No filesystem in the happy path is touched except a temp file we create here.
// -----------------------------------------------------------------------
public sealed class HostPublicKeyProviderTests
{
    [Fact]
    public void Load_WhenA32BytePublicKeyFileExists_ReturnsTheKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrDxtPub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "manifest-public.key");
            var key = new byte[32];
            new Random(7).NextBytes(key);
            File.WriteAllBytes(path, key);

            // enforceTrust:false — this test exercises the byte-length logic in a temp dir
            // the (non-admin) test user owns; the at-rest ACL enforcement is covered by
            // Load_WhenPresentButRootNotAdminTrusted_FailsClosed below.
            var result = HostPublicKeyProvider.Load(path, enforceTrust: false);

            result.IsOk.Should().BeTrue();
            result.Value.Should().Equal(key);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_WhenFileAbsent_ReturnsFail()
    {
        var path = Path.Combine(Path.GetTempPath(), "DrDxtPub_missing_" + Guid.NewGuid().ToString("N") + ".key");

        var result = HostPublicKeyProvider.Load(path);

        result.IsOk.Should().BeFalse("no provisioned key → fail (the host refuses to reconcile, fail-closed)");
        result.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void Load_WhenWrongLength_ReturnsFail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrDxtPub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "manifest-public.key");
            File.WriteAllBytes(path, new byte[16]); // not 32

            var result = HostPublicKeyProvider.Load(path, enforceTrust: false);

            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 11/09/2026 — un fichier provisionne qui contient la cle de DEV est refuse par le service, dans
    // toutes les configurations : sa graine privee vit dans les tests du depot. Le temoin est
    // Load_WhenA32BytePublicKeyFileExists_ReturnsTheKey ci-dessus (meme longueur, autre cle).
    [Fact]
    public void Load_WhenTheFileHoldsTheDevKey_ReturnsFail_InEveryConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrDxtPub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "manifest-public.key");
            File.WriteAllBytes(path, DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey);

            var result = HostPublicKeyProvider.Load(path, enforceTrust: false);

            result.IsOk.Should().BeFalse("the dev key is never trusted from a provisioned file");
            result.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DefaultPath_IsUnderTheAdminWriteManifestRoot()
    {
        HostPublicKeyProvider.DefaultPath().Should().EndWith("manifest-public.key");
    }

    // At-rest integrity (BLOCKER #1a): a 32-byte key that is well-formed but sits in a
    // root the (non-admin) user OWNS / can write is an attack (a forged key the user can
    // swap), not a provisioned key. With enforceTrust:true it FAILS CLOSED — so the host
    // never builds a verifier from a user-controlled key. Deterministic regardless of the
    // runner's elevation: the dir owner is explicitly forced to the current user SID, which
    // is never BUILTIN\Administrators.
    [Fact]
    public void Load_WhenPresentButRootNotAdminTrusted_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtPubUntrusted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.SetOwner(me); // force owner = the current (non-admin) user, never Administrators
            sec.AddAccessRule(new FileSystemAccessRule(
                me, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);

            var path = Path.Combine(dir, "manifest-public.key");
            var key = new byte[32];
            new Random(11).NextBytes(key);
            File.WriteAllBytes(path, key);

            var result = HostPublicKeyProvider.Load(path, enforceTrust: true);

            result.IsOk.Should().BeFalse(
                "a key in a user-owned/user-writable root is not admin-trusted at rest → fail-closed");
            result.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
