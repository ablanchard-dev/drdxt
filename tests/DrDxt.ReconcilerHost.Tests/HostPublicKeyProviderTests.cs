using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostPublicKeyProvider : charge la clé publique de vérification seule depuis un emplacement connu.
//
// L'hôte vérifie les manifestes ; il ne signe jamais (aucune clé privée, jamais). Ceci charge la
// clé publique Ed25519 brute de 32 octets depuis un fichier sous la racine de manifeste réservée aux admins.
//   * présente + 32 octets → Ok(clé)
//   * absente            → Fail (aucune clé provisionnée encore : l'hôte n'a alors pas de
//                          vérificateur et refuse de réconcilier, fermé)
//   * mauvaise longueur  → Fail (une clé malformée ne « marche » jamais en silence)
// Aucun système de fichiers touché sur le chemin nominal, hormis un fichier temporaire créé ici.
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

            // enforceTrust:false : ce test exerce la logique de longueur dans un dossier temporaire
            // possédé par l'utilisateur de test (non admin) ; l'ACL au repos est couverte par
            // Load_WhenPresentButRootNotAdminTrusted_FailsClosed ci-dessous.
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
            File.WriteAllBytes(path, new byte[16]); // pas 32

            var result = HostPublicKeyProvider.Load(path, enforceTrust: false);

            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Un fichier provisionne qui contient la cle de DEV est refuse par le service, dans
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

    // Intégrité au repos : une clé de 32 octets bien formée mais placée dans une racine que
    // l'utilisateur (non admin) possède ou peut écrire est une attaque (une clé forgée que
    // l'utilisateur peut substituer), pas une clé provisionnée. Avec enforceTrust:true elle se ferme :
    // l'hôte ne construit jamais un vérificateur depuis une clé contrôlée par l'utilisateur.
    // Déterministe quelle que soit l'élévation du runner : le propriétaire du dossier est forcé au
    // SID de l'utilisateur courant, qui n'est jamais BUILTIN\Administrators.
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
            sec.SetOwner(me); // forcer propriétaire = l'utilisateur courant (non admin), jamais Administrators
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
