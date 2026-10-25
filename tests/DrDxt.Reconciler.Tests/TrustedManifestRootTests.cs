using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Intégrité au repos de la racine de manifeste à laquelle SYSTEM fait confiance.
//
// La décision de confiance est une fonction pure de (SID propriétaire, ACE), donc la logique
// se vérifie ici avec des entrées synthétiques, sans admin ni vrai système de fichiers. Un petit
// contrôle sur disque prouve ensuite que le vrai EnsureTrustedDirectory rejette un dossier dont
// le propriétaire est forcé à l'utilisateur courant (non admin), quelle que soit l'élévation du runner.
// -----------------------------------------------------------------------
public sealed class TrustedManifestRootTests
{
    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier SystemSid => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AuthUsers => new(WellKnownSidType.AuthenticatedUserSid, null);
    // A fabricated, non-well-known account SID standing in for "some user".
    private static SecurityIdentifier SomeUser => new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private static TrustedManifestRoot.AceView Allow(SecurityIdentifier sid, FileSystemRights rights)
        => new(sid, rights, AccessControlType.Allow);

    [Fact]
    public void Trusted_WhenAdminOwner_AdminAndSystemWrite_UsersReadOnly()
    {
        var aces = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(AuthUsers, FileSystemRights.ReadAndExecute),
        };

        TrustedManifestRoot.Evaluate("root", Admins, aces).IsOk.Should().BeTrue(
            "admin-owned with write only for Administrators/SYSTEM and read-only for users is the hardened shape");
    }

    [Fact]
    public void Untrusted_WhenOwnerIsAUser()
    {
        var aces = new[] { Allow(Admins, FileSystemRights.FullControl) };

        var r = TrustedManifestRoot.Evaluate("root", SomeUser, aces);
        r.IsOk.Should().BeFalse("a user-owned root holds an implicit WRITE_DAC → not trusted");
        r.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void Untrusted_WhenOwnerIsNull()
    {
        TrustedManifestRoot.Evaluate("root", null, Array.Empty<TrustedManifestRoot.AceView>())
            .IsOk.Should().BeFalse("an unreadable owner is fail-closed, never trusted");
    }

    [Theory]
    [InlineData(FileSystemRights.Write)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    [InlineData(FileSystemRights.ChangePermissions)]   // WRITE_DAC
    [InlineData(FileSystemRights.TakeOwnership)]        // WRITE_OWNER
    [InlineData(FileSystemRights.Delete)]
    public void Untrusted_WhenANonAdminPrincipalHasAnyWriteOrControlGrant(FileSystemRights userRights)
    {
        var aces = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(SomeUser, userRights), // un utilisateur peut écrire, supprimer ou changer l'ACL → empoisonnable
        };

        TrustedManifestRoot.Evaluate("root", Admins, aces).IsOk.Should().BeFalse(
            $"a non-admin '{userRights}' grant makes the root user-writable → not trusted");
    }

    [Fact]
    public void Trusted_IgnoresNonAdminReadOnlyAndDenyAces()
    {
        var aces = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(AuthUsers, FileSystemRights.ReadAndExecute),
            new TrustedManifestRoot.AceView(SomeUser, FileSystemRights.FullControl, AccessControlType.Deny),
        };

        TrustedManifestRoot.Evaluate("root", Admins, aces).IsOk.Should().BeTrue(
            "a read-only allow and a DENY ace for a user do not make the root writable");
    }

    [Fact]
    public void BuildHardenedSecurity_PinsOwnerToAdministrators()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var owner = TrustedManifestRoot.BuildHardenedSecurity()
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        owner.Should().NotBeNull();
        owner!.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid).Should().BeTrue(
            "the creator must not stay owner (and keep WRITE_DAC) — the owner is pinned to Administrators");
    }

    [Fact]
    public void EnsureTrustedDirectory_OnAUserOwnedDir_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtTrust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.SetOwner(me); // force owner = current (non-admin) user, never Administrators
            di.SetAccessControl(sec);

            TrustedManifestRoot.EnsureTrustedDirectory(dir).IsOk.Should().BeFalse(
                "a user-owned root is rejected at rest (fail-closed)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Le descripteur de fichier durci fige owner=Administrators ─────────────────
    [Fact]
    public void BuildHardenedFileSecurity_PinsOwnerToAdministrators()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var owner = TrustedManifestRoot.BuildHardenedFileSecurity()
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        owner.Should().NotBeNull();
        owner!.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid).Should().BeTrue(
            "a trusted file an admin USER writes must be owned by the Administrators group, " +
            "not the user's S-1-5-21 SID, or the SYSTEM reconciler would reject a legitimate root (F3)");
    }

    // ── Une racine existante squattée, possédée par l'utilisateur, est réparée (élevé) ou échoue
    // bruyamment (non élevé) ; elle n'est jamais rendue Ok tant qu'elle reste non fiable. Déterministe
    // sur les deux runners : un jeton non admin ne peut pas poser owner=Administrators (Administrators
    // est refus seulement), donc la réparation échoue ; un jeton admin/SYSTEM la répare en possession admin.
    [Fact]
    public void EnsureCreatedHardened_OnUserOwnedExistingRoot_NeverReturnsOkOnUntrusted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtSquat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Pre-squat: owner = current (non-admin) user + a user-writable ACE.
            var me = WindowsIdentity.GetCurrent().User!;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.SetOwner(me);
            sec.AddAccessRule(new FileSystemAccessRule(
                me, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);

            // Précondition : elle est bien non fiable avant l'appel ensure.
            TrustedManifestRoot.EnsureTrustedDirectory(dir).IsOk.Should().BeFalse(
                "the squatted root must start untrusted for this test to mean anything");

            var result = TrustedManifestRoot.EnsureCreatedHardened(dir);

            if (result.IsOk)
            {
                // Légitime seulement élevé : Ok doit vouloir dire que la racine a été réparée en
                // possession admin. Ok sur une racine encore non fiable serait le défaut.
                TrustedManifestRoot.EnsureTrustedDirectory(dir).IsOk.Should().BeTrue(
                    "Ok must mean the squatted root was repaired to admin-owned, never trusted as-is (F1)");
            }
            else
            {
                // Expected on a non-elevated runner: FAIL LOUD, never a silent Ok.
                result.Error!.Value.Code.Should().BeOneOf(ErrorCodes.IoFailure, ErrorCodes.SignatureInvalid);
                TrustedManifestRoot.EnsureTrustedDirectory(dir).IsOk.Should().BeFalse(
                    "a failed repair leaves the root untrusted (fail-closed), never half-trusted (F1)");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* repaired DACL may block a non-admin delete */ }
        }
    }

    // ── Une écriture durcie donne un fichier possédé par un admin (élevé) ou échoue bruyamment
    // sans laisser de fichier à moitié écrit possédé par l'utilisateur (non élevé). Couvre le chemin
    // d'écriture du manifeste et de la clé (même aide). Le complément « un fichier possédé par
    // l'utilisateur est rejeté par Evaluate » est couvert par Untrusted_WhenOwnerIsAUser et le test disque de HostPublicKeyProvider.
    [Fact]
    public void WriteTrustedFile_ProducesAdminOwnedFile_OrFailsLoudly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtWriteTrust_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "manifest-public.key");
        try
        {
            var result = TrustedManifestRoot.WriteTrustedFile(path, new byte[32]);

            if (result.IsOk)
            {
                TrustedManifestRoot.EnsureTrustedFile(path).IsOk.Should().BeTrue(
                    "Ok must mean the written file is admin-owned at rest (F3)");
            }
            else
            {
                result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
                File.Exists(path).Should().BeFalse(
                    "a failed trusted write leaves no half-written user-owned file behind (F3)");
            }
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Le manifeste et le journal d'écriture anticipée passent par ici : un lecteur (l'App, le service) qui tient la cible au
    // moment du remplacement ne fait pas échouer l'écriture, elle reprend.
    [Fact]
    public async Task WriteTrustedFile_reprend_quand_un_lecteur_tient_la_cible()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtWriteTrustLecteur_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "desired-state.manifest");
        try
        {
            if (!TrustedManifestRoot.WriteTrustedFile(path, [1]).IsOk) return; // non élevé : couvert par le test précédent

            Result ecrit;
            using (var lecteur = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var ecriture = Task.Run(() => TrustedManifestRoot.WriteTrustedFile(path, [2]));
                await Task.Delay(100);
                lecteur.Dispose();
                ecrit = await ecriture;
            }

            ecrit.IsOk.Should().BeTrue();
            File.ReadAllBytes(path).Should().Equal(2);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Une racine point de reparse (jonction/lien symbolique) est rejetée avant la confiance. L'OS
    // la suivrait vers un stockage contrôlé par l'attaquant, donc le descripteur lu serait celui de la
    // cible, pas de la racine. Créer un lien symbolique demande SeCreateSymbolicLinkPrivilege ou le mode
    // développeur ; si le runner ne l'a pas, le test est filtré par plateforme (sauté) : fermé dans les deux cas.
    [Fact]
    public void EnsureTrustedDirectory_OnAReparsePoint_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var target = Path.Combine(Path.GetTempPath(), "DrDxtRpTarget_" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "DrDxtRpLink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch { return; /* needs SeCreateSymbolicLink / Developer Mode — platform-gated skip */ }

            var r = TrustedManifestRoot.EnsureTrustedDirectory(link);
            r.IsOk.Should().BeFalse(
                "a reparse-point root is followed silently by the OS → rejected (fail-closed, F2)");
            r.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { }
        }
    }

    // ── Un fichier point de reparse (lien symbolique) est rejeté avant la confiance, même raisonnement.
    [Fact]
    public void EnsureTrustedFile_OnAReparsePointFile_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "DrDxtRpFile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var realFile = Path.Combine(dir, "real.key");
        File.WriteAllBytes(realFile, new byte[32]);
        var link = Path.Combine(dir, "manifest-public.key");
        try
        {
            try { File.CreateSymbolicLink(link, realFile); }
            catch { return; /* platform-gated skip (symlink privilege) */ }

            TrustedManifestRoot.EnsureTrustedFile(link).IsOk.Should().BeFalse(
                "a reparse-point FILE is rejected before trust (fail-closed, F2)");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Un ACE peut porter le masque GENERIQUE brut (GENERIC_WRITE /
    // GENERIC_ALL) au lieu des bits specifiques mappes. Ces bits ne recoupent aucun droit
    // specifique, donc avant le correctif Evaluate jugeait "de confiance" un non-admin qui
    // detient GENERIC_WRITE/GENERIC_ALL sur la racine — soit exactement le grant d'ecriture
    // que cette garde existe pour refuser. GENERIC_READ/EXECUTE restent lecture seule.
    [Theory]
    [InlineData(0x40000000)] // GENERIC_WRITE
    [InlineData(0x10000000)] // GENERIC_ALL
    public void Untrusted_WhenANonAdminHoldsRawGenericWriteOrAll(int genericMask)
    {
        var aces = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(SomeUser, (FileSystemRights)genericMask),
        };

        TrustedManifestRoot.Evaluate("root", Admins, aces).IsOk.Should().BeFalse(
            $"a non-admin holding the raw generic mask 0x{genericMask:X8} can rewrite the trust root");
    }

    // Complément : GENERIC_READ / GENERIC_EXECUTE sont en lecture seule et ne doivent pas déclencher
    // la garde (un lecteur peu privilégié lit légitimement la clé), sinon le correctif ferait un faux positif.
    [Theory]
    [InlineData(0x20000000)] // GENERIC_EXECUTE
    public void Trusted_WhenNonAdminHoldsOnlyGenericReadOrExecute(int genericMask)
    {
        var aces = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(SomeUser, (FileSystemRights)genericMask),
        };

        TrustedManifestRoot.Evaluate("root", Admins, aces).IsOk.Should().BeTrue(
            $"a non-admin read/execute-only generic grant 0x{genericMask:X8} is not a write grant");
    }

}
