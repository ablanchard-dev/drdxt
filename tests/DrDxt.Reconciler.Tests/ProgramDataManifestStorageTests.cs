using System.Runtime.InteropServices;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Le vrai stockage ProgramData inscriptible par les seuls administrateurs.
//
// Le chemin et le descripteur DACL sont des valeurs pures et testables (ni système de
// fichiers ni admin), donc les tests unitaires vérifient la forme de sécurité : la racine
// est sous ProgramData, la DACL n'accorde l'écriture qu'aux Administrateurs et à SYSTEM et la
// lecture à tous, et elle n'est pas inscriptible par l'utilisateur. Un vrai aller-retour
// atomique en dossier temporaire s'exerce en option (sans admin) pour prouver la logique écriture/lecture/atomicité.
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
            return; // le modèle d'ACL est propre à Windows ; la logique se vérifie sur les runners Windows.

        var dacl = ProgramDataManifestStorage.BuildAdminWriteDacl();
        var sddl = dacl.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

        // Les administrateurs (BA) et SYSTEM (SY) ont le contrôle total ; le monde large (WD/AU/BU)
        // ne doit pas apparaître avec un droit d'écriture ou de modification. La lecture pour tous convient.
        sddl.Should().Contain("BA", "Administrators must be in the DACL");
        sddl.Should().Contain("SY", "SYSTEM must be in the DACL");

        // Aucune ACE d'autorisation accordant l'écriture (FA/FW/0x...2/0x...6) à Users/Authenticated/World.
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
        // Un vrai aller-retour sur disque dans un dossier temporaire isolé (sans admin, sans
        // ProgramData) : prouve WriteAsync (temporaire + remplacement), ReadAsync et le fichier absent.
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "DrDxtReconcilerTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ProgramDataManifestStorage(tempRoot, applyAcl: false);

            // Fichier absent → Ok(null).
            (await storage.ReadAsync(CancellationToken.None)).Value.Should().BeNull();

            var env = new SignedManifestEnvelope("{\"hello\":1}", "c2ln");
            (await storage.WriteAsync(env, CancellationToken.None)).IsOk.Should().BeTrue();

            var read = await storage.ReadAsync(CancellationToken.None);
            read.IsOk.Should().BeTrue();
            read.Value.Should().Be(env);

            // L'écrasement est atomique (aucun .tmp qui traîne).
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

    // Un fichier PRESENT qui contient « null » ne se lit pas comme « aucun manifeste » (premiere
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

    // Le chemin d'écriture de production (applyAcl:true) fige le propriétaire du fichier
    // manifeste. Élevé, le manifeste finit possédé par un admin (le réconciliateur SYSTEM lui
    // fait confiance au repos) ; non élevé, il échoue bruyamment sans laisser de manifeste
    // possédé par l'utilisateur (jamais une racine que le service rejetterait en silence). Déterministe sur les deux runners.
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
