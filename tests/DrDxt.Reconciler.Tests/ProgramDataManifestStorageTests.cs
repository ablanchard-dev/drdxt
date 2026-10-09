using System.Runtime.InteropServices;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T2 — the real ProgramData admin-write-only storage.
//
// The PATH + the DACL descriptor are pure, testable values (no FS / no admin
// needed) so unit tests assert the security shape: the root is under ProgramData,
// the DACL grants write to Administrators + SYSTEM only and read to everyone, and
// it is not user-writable. A real temp-dir atomic round-trip is exercised opt-in
// (no admin required) to prove the write/read/atomic logic.
// -----------------------------------------------------------------------
public sealed class ProgramDataManifestStorageTests
{
    [Fact]
    public void DefaultRoot_LivesUnderProgramData()
    {
        var root = ProgramDataManifestStorage.DefaultRoot();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        root.Should().StartWith(programData);
        root.Should().Contain("DrDxt");
    }

    [Fact]
    public void AdminWriteDacl_GrantsWriteOnlyToAdminsAndSystem_notUsers()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // the ACL model is Windows-only; logic is asserted on Windows runners.

        var dacl = ProgramDataManifestStorage.BuildAdminWriteDacl();
        var sddl = dacl.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

        // Admins (BA) and SYSTEM (SY) get full control; the broad world (WD/AU/BU)
        // must not appear with a write/modify grant. Read for everyone is fine.
        sddl.Should().Contain("BA", "Administrators must be in the DACL");
        sddl.Should().Contain("SY", "SYSTEM must be in the DACL");

        // No allow-ACE granting write (FA/FW/0x...2/0x...6) to Users/Authenticated/World.
        dacl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Where(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                        && (r.FileSystemRights & System.Security.AccessControl.FileSystemRights.Write) != 0)
            .Select(r => ((System.Security.Principal.SecurityIdentifier)r.IdentityReference)
                .IsWellKnown(System.Security.Principal.WellKnownSidType.WorldSid)
                || ((System.Security.Principal.SecurityIdentifier)r.IdentityReference)
                .IsWellKnown(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid)
                || ((System.Security.Principal.SecurityIdentifier)r.IdentityReference)
                .IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid))
            .Should().NotContain(true, "no broad principal may have WRITE on the manifest root");
    }

    [Fact]
    public async Task RealRoundTrip_InTempDir_WritesAtomicallyAndReadsBack()
    {
        // A real filesystem round-trip in an isolated temp dir (no admin, no
        // ProgramData) — proves WriteAsync (temp+replace) + ReadAsync + absent-file.
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "DrDxtReconcilerTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ProgramDataManifestStorage(tempRoot, applyAcl: false);

            // Absent file → Ok(null).
            (await storage.ReadAsync(CancellationToken.None)).Value.Should().BeNull();

            var env = new SignedManifestEnvelope("{\"hello\":1}", "c2ln");
            (await storage.WriteAsync(env, CancellationToken.None)).IsOk.Should().BeTrue();

            var read = await storage.ReadAsync(CancellationToken.None);
            read.IsOk.Should().BeTrue();
            read.Value.Should().Be(env);

            // Overwrite is atomic (no leftover .tmp).
            var env2 = env with { CanonicalJson = "{\"hello\":2}" };
            (await storage.WriteAsync(env2, CancellationToken.None)).IsOk.Should().BeTrue();
            (await storage.ReadAsync(CancellationToken.None)).Value.Should().Be(env2);
            Directory.GetFiles(tempRoot, "*.tmp").Should().BeEmpty("the temp file is moved, never left behind");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    // 26/09/2026 : un fichier PRESENT qui contient « null » se lisait comme « aucun manifeste » (premiere
    // installation). Un fichier abime n'est pas un fichier absent.
    [Fact]
    public async Task Un_fichier_present_qui_contient_null_n_est_pas_un_fichier_absent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DrDxtReconcilerTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ProgramDataManifestStorage(tempRoot, applyAcl: false);
            (await storage.WriteAsync(new SignedManifestEnvelope("{}", "c2ln"), CancellationToken.None)).IsOk.Should().BeTrue();
            File.WriteAllText(Directory.GetFiles(tempRoot).Single(), "null");

            var read = await storage.ReadAsync(CancellationToken.None);

            read.IsOk.Should().BeFalse();
            read.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    // F3 — the PRODUCTION write path (applyAcl:true) pins the manifest file owner. When
    // elevated the manifest ends up admin-owned (so the SYSTEM reconciler trusts it at
    // rest); when not elevated it FAILS LOUD with no user-owned manifest left behind
    // (never a root the service would silently reject). Deterministic on either runner.
    [Fact]
    public async Task WriteAsync_WithAcl_PinsManifestOwnerToAdmins_OrFailsLoudly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), "DrDxtManifestAcl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ProgramDataManifestStorage(root, applyAcl: true);
            var env = new SignedManifestEnvelope("{\"hello\":1}", "c2ln");

            var result = await storage.WriteAsync(env, CancellationToken.None);
            var filePath = Path.Combine(root, "desired-state.manifest");

            if (result.IsOk)
            {
                TrustedManifestRoot.EnsureTrustedFile(filePath).IsOk.Should().BeTrue(
                    "Ok must mean the manifest is admin-owned at rest (F3)");
            }
            else
            {
                result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
                File.Exists(filePath).Should().BeFalse(
                    "a failed hardened manifest write leaves no user-owned file the SYSTEM service would reject (F3)");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
