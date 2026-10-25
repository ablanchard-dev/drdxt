using System;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY : aucun secret ne doit jamais vivre ici, dans aucune configuration.
// ───────────────────────────────────────────────────────────────────────────────
//
// Ce fichier est la racine de confiance client / service du manifeste du réconciliateur. Il ne contient que du matériel sûr à livrer dans un binaire que
// n'importe qui peut décompiler :
//   * la clé publique Ed25519 brute de 32 octets qui vérifie le manifeste : elle ne peut que
//     vérifier un manifeste d'état voulu signé, jamais en frapper un. L'App + l'hôte
//     réconciliateur en construisent leur vérificateur pour qu'un build d'équipe autonome vérifie
//     de vrais manifestes.
//
// Aucun octet de clé privée ici. Une clé privée de signature du manifeste utilisable est
// interdite dans tout chemin de code livré ; et contrairement à une barrière #if DEBUG, ce
// fichier ne porte aucun octet de graine, ni en Debug ni en Release. La graine privée de dev/test
// avec laquelle les tests unitaires signent leurs manifestes d'essai ne vit que dans les
// assemblages de test (DrDxt.TestSupport.ManifestDevTestKeys, tests/Shared) : un
// DrDxt.Reconciler.dll livré ne contient aucun octet de clé privée utilisable.
//
// La
// vraie cle de signature est propre a chaque machine (MachineManifestKey), creee par le helper
// eleve au premier CommitManifestOp sous %ProgramData%\DrDxt (Administrateurs + SYSTEM
// seulement), et `manifest-public.key` en est derivee. Cette cle de dev n'est jamais acceptee
// ailleurs que dans le repli embarque sous #if DEBUG : un fichier provisionne qui la contient est
// refuse (EstLaCleDeDev), et une cle de machine qui la deriverait aussi. Un build Release sans
// cle fournie refuse (fail-closed) : un manifeste signe par la cle de dev ou non signe est rejete.
// ───────────────────────────────────────────────────────────────────────────────
public static class ManifestDevKeys
{
    /// <summary>La clé publique Ed25519 brute de 32 octets. Sûre à commiter : elle ne peut que
    /// vérifier, jamais signer. L'App + l'hôte réconciliateur en construisent leur vérificateur pour
    /// vérifier de vrais manifestes dans un build Debug / d'équipe autonome. Un build Release ne lui
    /// fait pas confiance : il exige une clé publique de production fournie (voir l'en-tête) et sinon
    /// refuse (fail-closed). La graine privée de dev/test assortie est réservée aux tests et vit dans
    /// DrDxt.TestSupport.ManifestDevTestKeys (tests/Shared), jamais dans cet assemblage livré.</summary>
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

    /// <summary>Construit l'<see cref="IManifestVerifier"/> en vérification seule à partir de la clé
    /// publique embarquée. N'échoue jamais (la clé embarquée est bien formée par construction) ;
    /// emballé dans un Result pour qu'un futur échange par une clé fournie garde le même contrat
    /// d'échec propre. Commodité Debug / test seulement : un build Release vérifie avec une clé
    /// fournie et ne se rabat jamais sur celle-ci (voir HostServiceCollectionExtensions / AppManifestPublicKey).</summary>
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
