using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// MachineManifestKey : la cle de signature du manifeste, propre a cette machine.
//
// Pourquoi une cle par machine. La regle du projet est « rien hors du PC » : pas de signataire serveur.
// La graine en variable d'environnement ne sert a personne sur une machine installee. Il reste une
// cle creee localement, par le seul processus qui a le droit d'ecrire la racine admin : le helper
// eleve, au premier `CommitManifestOp`. L'App (integrite moyenne) ne la tient jamais.
//
// Ou elle vit. `%ProgramData%\DrDxt\manifest-signing.key`, dans la racine deja durcie par la revue de
// securite (proprietaire Administrateurs, ACE, points de reparse : TrustedManifestRoot). Le fichier a
// sa propre ACL, plus stricte que celle des fichiers publics : Administrateurs + SYSTEM seulement,
// posee a la creation (TrustedManifestRoot.BuildPrivateFileSecurity), verifiee a chaque lecture
// (EnsurePrivateFile : un fichier lisible par un non-admin est refuse).
//
// DPAPI, et ce qu'il ne fait pas. La graine est chiffree DPAPI portee machine. Cette portee se
// dechiffre depuis n'importe quel compte local : face a un processus local, DPAPI ne protege rien, et
// c'est l'ACL qui garde la cle. DPAPI ne sert qu'a rendre inutilisable une copie du fichier sortie de
// la machine (sauvegarde, image disque, archive de support) : il faudrait aussi la cle maitresse de
// cette machine. Deux lignes pour cela ; ce n'est pas la garantie.
//
// La cle privee est la source de verite. La cle publique (`manifest-public.key`, lue par l'App et par le
// service) en est derivee et reecrite si elle differe. Ordre d'ecriture : cle privee, puis manifeste,
// puis cle publique : une coupure entre les deux derniers laisse une publique perimee, reparee au
// commit suivant ; jamais une cle privee perdue.
//
// Ce qui n'est jamais fait en silence. Une cle privee presente mais inutilisable (ACL non fiable,
// illisible, ou c'est la cle de dev) n'est ni ecrasee ni remplacee : refus nomme
// (ErrorCodes.ManifestKeyUnusable). Seule la reprise explicite la met de cote (renommee, jamais
// effacee) et en cree une neuve.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class MachineManifestKey
{
    public const string PrivateFileName = "manifest-signing.key";
    public const string PublicFileName = "manifest-public.key";

    private const int SeedLength = 32;

    // L'entropie n'est pas un secret (elle est dans le binaire) : elle separe seulement ce blob des autres
    // blobs DPAPI machine de ce poste.
    private static readonly byte[] Entropie = "DrDXT.manifest-signing.v1"u8.ToArray();

    public sealed record Paire(IManifestSigner Signataire, IManifestVerifier Verificateur, byte[] ClePublique);

    /// <summary>La cle de la machine : chargee si elle existe, creee si elle n'existe pas. Presente mais
    /// inutilisable ⇒ <see cref="ErrorCodes.ManifestKeyUnusable"/>, et rien n'est touche.</summary>
    /// <param name="appliquerAcl">Production : true. Les tests passent false (dossier temporaire, aucune
    /// ACL posee ni verifiee) : la decision d'ACL est prouvee a part, en fonction pure.</param>
    public static Result<Paire> ChargerOuCreer(string racine, bool appliquerAcl)
    {
        var chemin = Path.Combine(racine, PrivateFileName);
        try
        {
            if (!File.Exists(chemin))
                return Creer(chemin, appliquerAcl);

            if (appliquerAcl)
            {
                var fiable = TrustedManifestRoot.EnsurePrivateFile(chemin);
                if (!fiable.IsOk)
                    return Inutilisable($"its file is not private at rest: {fiable.Error!.Value.Message}");
            }

            byte[] graine;
            try
            {
                graine = ProtectedData.Unprotect(File.ReadAllBytes(chemin), Entropie, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException ex)
            {
                return Inutilisable($"it could not be decrypted on this machine: {ex.Message}");
            }

            try { return DepuisGraine(graine); }
            finally { Array.Clear(graine); }
        }
        catch (Exception ex)
        {
            return Inutilisable($"it could not be read: {ex.Message}");
        }
    }

    /// <summary>Pose (ou repare) la cle publique a cote de la privee. Un fichier deja identique et fiable
    /// au repos n'est pas reecrit ; un fichier identique mais plante par un non-admin l'est (l'App et le
    /// service le refuseraient).</summary>
    public static Result PublierClePublique(string racine, bool appliquerAcl, byte[] clePublique)
    {
        var chemin = Path.Combine(racine, PublicFileName);
        try
        {
            if (File.Exists(chemin)
                && File.ReadAllBytes(chemin).AsSpan().SequenceEqual(clePublique)
                && (!appliquerAcl || TrustedManifestRoot.EnsureTrustedFile(chemin).IsOk))
                return Result.Ok();
        }
        catch (Exception)
        {
            // Illisible : on le reecrit ci-dessous.
        }
        return Ecrire(chemin, clePublique, appliquerAcl, prive: false);
    }

    /// <summary>La desinstallation relit le manifeste avec la cle publique de la machine, comme
    /// l'App et le service. Elle ne cree jamais de cle : absente ⇒ rien n'a jamais ete signe ici. Un fichier
    /// non fiable au repos, de mauvaise longueur, ou qui contient la cle de dev est refuse.</summary>
    public static Result<IManifestVerifier> ChargerVerificateur(string racine, bool appliquerAcl)
    {
        var chemin = Path.Combine(racine, PublicFileName);
        try
        {
            if (!File.Exists(chemin))
                return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                    "No machine public key: nothing was ever signed on this machine.");
            if (appliquerAcl)
            {
                var fiable = TrustedManifestRoot.EnsureTrustedFile(chemin);
                if (!fiable.IsOk)
                    return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                        $"The machine public key is not admin-trusted at rest: {fiable.Error!.Value.Message}");
            }

            var cle = File.ReadAllBytes(chemin);
            if (cle.Length != 32)
                return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                    $"The machine public key has the wrong length ({cle.Length}).");
            if (ManifestDevKeys.EstLaCleDeDev(cle))
                return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                    "The machine public key is the development key; it is never trusted.");
            return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(cle));
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"The machine public key could not be read: {ex.Message}");
        }
    }

    /// <summary>La reprise : la cle privee inutilisable est renommee a cote, jamais effacee.</summary>
    public static Result MettreDeCote(string racine) => MiseDeCote.Renommer(Path.Combine(racine, PrivateFileName));

    private static Result<Paire> Creer(string chemin, bool appliquerAcl)
    {
        var graine = RandomNumberGenerator.GetBytes(SeedLength);
        try
        {
            var paire = DepuisGraine(graine);
            if (!paire.IsOk)
                return paire;

            var ecrit = Ecrire(chemin, ProtectedData.Protect(graine, Entropie, DataProtectionScope.LocalMachine),
                appliquerAcl, prive: true);
            return ecrit.IsOk ? paire : Result<Paire>.Fail(ecrit.Error!.Value.Code, ecrit.Error.Value.Message);
        }
        finally
        {
            Array.Clear(graine);
        }
    }

    private static Result<Paire> DepuisGraine(byte[] graine)
    {
        if (graine.Length != SeedLength)
            return Inutilisable($"its seed has the wrong length ({graine.Length}).");

        var cle = Key.Import(SignatureAlgorithm.Ed25519, graine, KeyBlobFormat.RawPrivateKey);
        var publique = cle.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // Une cle de machine n'est jamais la cle de dev : sa graine privee est dans les tests du depot.
        if (ManifestDevKeys.EstLaCleDeDev(publique))
        {
            cle.Dispose();
            return Inutilisable("it is the development key, which is never trusted on a machine.");
        }

        return Result<Paire>.Ok(new Paire(new Ed25519ManifestSigner(cle), new Ed25519ManifestVerifier(publique), publique));
    }

    private static Result Ecrire(string chemin, byte[] octets, bool appliquerAcl, bool prive)
    {
        if (appliquerAcl)
            return TrustedManifestRoot.WriteTrustedFile(chemin, octets, prive);

        return new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic(octets);
    }

    private static Result<Paire> Inutilisable(string pourquoi)
        => Result<Paire>.Fail(ErrorCodes.ManifestKeyUnusable,
            $"The machine's manifest signing key exists but cannot be used: {pourquoi}");
}

/// <summary>Mettre de cote = renommer a cote, avec l'heure UTC. Jamais effacer : ce qui ne se relit plus
/// reste la seule trace de ce qu'il contenait, et la regle du depot interdit de supprimer.</summary>
internal static class MiseDeCote
{
    /// <param name="suffixe">pourquoi il est mis de cote : « mis-de-cote » (illisible, reprise) ou
    /// « rendu » (11/09/2026 : ses origines viennent d'etre rendues a la desinstallation). Le nom du
    /// fichier laisse dit ce qui lui est arrive.</param>
    public static Result Renommer(string chemin, string suffixe = "mis-de-cote")
    {
        try
        {
            if (!File.Exists(chemin))
                return Result.Ok();
            var cible = chemin + "." + suffixe + "-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            File.Move(chemin, cible, overwrite: false);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, $"Could not set '{chemin}' aside: {ex.Message}");
        }
    }
}
