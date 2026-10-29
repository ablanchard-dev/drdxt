using System;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY : aucun secret ne doit jamais vivre ici, dans aucune configuration.
// ───────────────────────────────────────────────────────────────────────────────
//
// La racine de confiance client / service du manifeste du réconciliateur, sûre à livrer dans un binaire décompilable :
//   * la clé publique Ed25519 brute de 32 octets, qui ne peut que vérifier un manifeste signé, jamais en frapper un.
// Aucun octet de clé privée ni de graine, ni en Debug ni en Release (plus strict qu'une barrière #if DEBUG) : la graine
// de dev/test des manifestes d'essai ne vit que dans les assemblages de test (DrDxt.TestSupport.ManifestDevTestKeys,
// tests/Shared).
// La vraie cle de signature est propre a chaque machine (MachineManifestKey), creee par le helper eleve au premier
// CommitManifestOp sous %ProgramData%\DrDxt (Administrateurs + SYSTEM seulement) ; `manifest-public.key` en est
// derivee. Cette cle de dev n'est acceptee que dans le repli embarque sous #if DEBUG : un fichier provisionne qui la
// contient est refuse (EstLaCleDeDev), une cle de machine qui la deriverait aussi. Un build Release sans cle fournie
// refuse (fail-closed) : un manifeste signe par la cle de dev ou non signe est rejete.
// ───────────────────────────────────────────────────────────────────────────────
public static class ManifestDevKeys
{
    /// <summary>La clé publique Ed25519 brute de 32 octets, sûre à commiter (elle ne fait que vérifier). L'App et l'hôte
    /// réconciliateur en vérifient les manifestes d'un build Debug / d'équipe autonome ; un build Release exige une clé
    /// de production fournie, sinon refuse (voir l'en-tête).</summary>
    public static byte[] PublicKey =>
    [
        0x97, 0xE0, 0xAC, 0x22, 0x73, 0xD9, 0xEF, 0xC2,
        0x9E, 0x7F, 0x2A, 0x11, 0x3F, 0x4D, 0x19, 0xE9,
        0x15, 0xC0, 0x80, 0x4B, 0x4C, 0x41, 0xE3, 0x4C,
        0xAE, 0x35, 0x81, 0x4C, 0xC4, 0xFE, 0xAE, 0x28,
    ];

    /// <summary>Cette cle publique est-elle la cle de dev ? Une cle de machine, un fichier
    /// de cle publique provisionne ou une cle derivee par le helper ne doivent jamais l'etre : sa graine
    /// privee vit dans les tests, donc quiconque a le depot signerait un manifeste accepte.</summary>
    public static bool EstLaCleDeDev(ReadOnlySpan<byte> clePublique) => clePublique.SequenceEqual(PublicKey);

    /// <summary>L'<see cref="IManifestVerifier"/> en vérification seule sur la clé embarquée. N'échoue pas (clé bien
    /// formée), Result pour garder le contrat d'une clé fournie. Debug / test seulement : un build Release ne s'y rabat
    /// jamais (voir HostServiceCollectionExtensions / AppManifestPublicKey).</summary>
    public static Result<IManifestVerifier> CreateVerifier()
    {
        try
        {
            return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(PublicKey));
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"Embedded manifest public key could not be imported: {ex.Message}");
        }
    }
}
