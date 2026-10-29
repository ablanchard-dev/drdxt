using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// TrustedManifestRoot : intégrité au repos de la racine du manifeste à laquelle SYSTEM fait confiance.
//
// Le réconciliateur de démarrage (LocalSystem) fait confiance à deux fichiers sous %ProgramData%\DrDxt :
// manifest-public.key (la racine de confiance, en vérification seule) et desired-state.manifest (l'état voulu signé).
// Si un non-admin peut remplacer la clé publique, il signe un manifeste falsifié avec la sienne : la protection ACL au
// repos s'impose donc avant toute confiance.
//   (création) Le propriétaire de la racine et des fichiers est épinglé à Administrateurs : un propriétaire détient un
//     WRITE_DAC implicite, et un fichier écrit par un admin non SYSTEM appartiendrait à son SID S-1-5-21 (rejeté).
//     Poser owner=Administrators exige un jeton élevé / SYSTEM : un écrivain non élevé échoue sans laisser de racine
//     possédée par l'utilisateur. Une racine squattée d'avance est réparée (RepairToTrusted) ou c'est l'échec ; jamais
//     Ok sur une racine non fiable.
//   (vérification) Pas de point de reparse (l'OS le suivrait vers un stockage de l'attaquant), propriétaire
//     Administrateurs/SYSTEM, aucun droit d'écriture ou de contrôle non admin. Fail-closed : au moindre doute, Fail, et
//     l'appelant ne réapplique rien.
// La décision est pure (Evaluate sur un propriétaire + des ACE), testée sans admin ni vrai système de fichiers.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class TrustedManifestRoot
{
    // Seuls Administrateurs + SYSTEM possèdent la racine ou y écrivent : rien d'autre ne l'écrit légitimement.
    private static bool IsTrustedPrincipal(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    // Les droits « d'écriture / de contrôle » : l'un d'eux à un non-admin rend la racine non fiable.
    private const FileSystemRights WriteishRights =
        FileSystemRights.WriteData                     // CreateFiles
        | FileSystemRights.AppendData                  // CreateDirectories
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions           // WRITE_DAC
        | FileSystemRights.TakeOwnership               // WRITE_OWNER
        // Une ACE peut porter le masque générique brut, qui ne recoupe pas les bits spécifiques : sans eux, GENERIC_WRITE
        // ou GENERIC_ALL passeraient pour « aucun droit d'écriture ». GENERIC_READ / GENERIC_EXECUTE : lecture, non signalés.
        | (FileSystemRights)0x40000000                 // GENERIC_WRITE
        | (FileSystemRights)0x10000000;                // GENERIC_ALL

    /// <summary>La sécurité durcie du dossier racine : propriétaire Administrateurs, DACL protégée (FullControl pour
    /// Administrateurs + SYSTEM, ReadAndExecute pour les utilisateurs authentifiés, aucun héritage). Ne s'applique que par
    /// du code élevé.</summary>
    public static DirectorySecurity BuildHardenedSecurity()
    {
        var security = new DirectorySecurity();
        // Cesser d'hériter d'une ACL parente (peut-être inscriptible par l'utilisateur).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Propriétaire Administrateurs : le créateur ne peut pas rouvrir la DACL par le WRITE_DAC implicite.
        security.SetOwner(admins);

        const InheritanceFlags inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Lecture seule pour tous les autres : l'App / un lecteur peu privilégié lit sans être admin.
        security.AddAccessRule(new FileSystemAccessRule(
            authUsers, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        return security;
    }

    /// <summary>La sécurité durcie d'un fichier de confiance (manifest-public.key, le manifeste signé) : comme le dossier,
    /// sans héritage. Sans propriétaire épinglé, un fichier écrit par un installeur admin appartiendrait à son SID
    /// S-1-5-21, et le réconciliateur SYSTEM le rejetterait. Code élevé seulement.</summary>
    public static FileSecurity BuildHardenedFileSecurity()
    {
        var security = new FileSecurity();
        // Cesser d'hériter d'une ACL parente (peut-être inscriptible par l'utilisateur).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Propriétaire Administrateurs, pas l'utilisateur qui écrit.
        security.SetOwner(admins);

        // Les fichiers ne portent aucun drapeau d'héritage (un fichier est une feuille : les appliquer lève).
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            authUsers, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        return security;
    }

    /// <summary>La securite d'un fichier prive (la cle de signature de la machine) : Administrateurs + SYSTEM seulement,
    /// aucune lecture utilisateur. La protection DPAPI « machine » se dechiffre depuis n'importe quel compte local : c'est
    /// cette ACL qui garde la cle.</summary>
    public static FileSecurity BuildPrivateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Crée la racine durcie si elle manque ; existante, la vérifie et la répare sinon. Jamais Ok sur une racine
    /// non fiable : un <c>%ProgramData%\DrDxt</c> squatté d'avance est réparé depuis un contexte élevé, sinon Fail. Un
    /// point de reparse n'est jamais réparé ni tenu pour fiable. Ne lève jamais.</summary>
    public static Result EnsureCreatedHardened(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // Un point de reparse redirigerait vers un stockage de l'attaquant : rejet immédiat.
                if (IsReparsePoint(dir))
                    return Result.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest root '{dir}' is a reparse point (junction/symlink): refusing to trust or repair.");

                // Déjà fiable au repos → rien à faire.
                var trust = EnsureTrustedDirectory(dir);
                if (trust.IsOk)
                    return Result.Ok();

                // Non fiable → réparer (élévation requise), sinon échec.
                var repaired = RepairToTrusted(dir);
                if (!repaired.IsOk)
                    return repaired;

                // Revérifier après réparation.
                return EnsureTrustedDirectory(dir);
            }

            new DirectoryInfo(dir).Create(BuildHardenedSecurity());
            // Défense en profondeur : revérifier la création (un jeton non élevé fait lever Create).
            return EnsureTrustedDirectory(dir);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not create or harden the manifest root '{dir}': {ex.Message}");
        }
    }

    /// <summary>Remet un dossier existant dans la protection fiable : prise de possession (owner=Administrators), puis
    /// DACL protégée. Seul un jeton élevé / SYSTEM (SID Administrateurs avec SE_GROUP_OWNER) le peut ; un jeton non élevé
    /// (Administrateurs en deny-only) rend Fail. Ne lève jamais.</summary>
    public static Result RepairToTrusted(string dir)
    {
        try
        {
            if (IsReparsePoint(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"'{dir}' is a reparse point (junction/symlink): refusing to repair.");

            var di = new DirectoryInfo(dir);

            // Étape 1 : prise de possession ; un jeton non élevé lève ici (→ Fail).
            var takeOwner = new DirectorySecurity();
            takeOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            di.SetAccessControl(takeOwner);

            // Étape 2 : propriétaire (⇒ WRITE_DAC implicite), épingler la DACL protégée sans héritage.
            di.SetAccessControl(BuildHardenedSecurity());
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not repair the manifest root '{dir}' to admin-owned (elevation required): {ex.Message}");
        }
    }

    /// <summary>Écrit atomiquement un fichier de confiance avec owner=Administrators et la DACL protégée, après s'être
    /// assuré de la racine parente. Propriétaire impossible à poser (appelant non élevé) : écriture partielle retirée,
    /// Fail ; jamais un fichier possédé par l'utilisateur. Ne lève jamais.</summary>
    /// <param name="prive">La cle de signature de la machine : creee directement avec
    /// <see cref="BuildPrivateFileSecurity"/>, puis verifiee par <see cref="EnsurePrivateFile"/>. Sinon, entre la creation
    /// du temporaire et la pose de son ACL, un processus local pourrait lire le blob DPAPI « machine ».</param>
    public static Result WriteTrustedFile(string path, byte[] bytes, bool prive = false)
    {
        // Nom aléatoire, créé en exclusif : un lien dur posé d'avance sous un nom prévisible ferait réécrire sa cible.
        var tmp = FichierJsonAtomique.NomTemporaire(path);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return Result.Fail(ErrorCodes.IoFailure,
                    $"Trusted file '{path}' has no parent directory.");

            // La racine doit appartenir aux admins avant qu'on y dépose un fichier de confiance.
            var ensured = EnsureCreatedHardened(dir);
            if (!ensured.IsOk)
                return ensured;

            if (prive)
            {
                // Le descripteur est pose a la creation : aucune fenetre ou le fichier herite du dossier.
                using var flux = new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.WriteThrough, BuildPrivateFileSecurity());
                flux.Write(bytes);
                flux.Flush(flushToDisk: true);
            }
            else
            {
                using (var flux = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    flux.Write(bytes);
                    // Sur le disque avant le renommage (journal d'écriture anticipée, manifeste), comme FichierJsonAtomique.
                    flux.Flush(flushToDisk: true);
                }
                // Épinglé avant la publication : le fichier de confiance n'appartient jamais, même un instant, à l'utilisateur.
                new FileInfo(tmp).SetAccessControl(BuildHardenedFileSecurity());
            }
            // Un renommage, qui garde le descripteur de sécurité, repris quelques fois quand un lecteur (l'App, le service)
            // tient la cible à cet instant.
            FichierJsonAtomique.Remplacer(tmp, path);

            // Défense en profondeur : le fichier publié doit être fiable au repos.
            var trust = prive ? EnsurePrivateFile(path) : EnsureTrustedFile(path);
            if (!trust.IsOk)
                return Result.Fail(ErrorCodes.IoFailure,
                    $"Trusted file '{path}' is not admin-owned after write: {trust.Error!.Value.Message}");

            return Result.Ok();
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return Result.Fail(ErrorCodes.IoFailure,
                $"Trusted file write failed for '{path}': {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup; the caller already returns Fail. */ }
    }

    /// <summary>Le chemin est-il un point de reparse (jonction / lien symbolique) ? Lève si les attributs sont illisibles :
    /// les appelants sont dans un try/catch fail-closed.</summary>
    internal static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Un dossier fiable au repos : il existe, propriétaire Administrateurs/SYSTEM, aucun droit d'écriture ou de
    /// contrôle non admin. Fail-closed.</summary>
    public static Result EnsureTrustedDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted manifest root '{dir}' does not exist.");

            // L'OS suit un point de reparse en silence : le SD lu serait celui de la cible.
            if (IsReparsePoint(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted manifest root '{dir}' is a reparse point (junction/symlink): not trusted.");

            var security = new DirectoryInfo(dir)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return Evaluate(dir, security);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SignatureInvalid,
                $"Trusted manifest root '{dir}' could not be inspected: {ex.Message}");
        }
    }

    /// <summary>Un fichier fiable au repos : propriétaire Administrateurs/SYSTEM, et un dossier parent fiable (sinon un
    /// non-admin l'échangerait en le recréant). Fail-closed.</summary>
    public static Result EnsureTrustedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted file '{path}' does not exist.");

            // Un lien symbolique mènerait vers une cible dont l'attaquant contrôle le SD et les octets.
            if (IsReparsePoint(path))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted file '{path}' is a reparse point (junction/symlink): not trusted.");

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted file '{path}' has no parent directory.");

            var dirTrust = EnsureTrustedDirectory(dir);
            if (!dirTrust.IsOk)
                return dirTrust;

            var security = new FileInfo(path)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return Evaluate(path, security);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SignatureInvalid,
                $"Trusted file '{path}' could not be inspected: {ex.Message}");
        }
    }

    /// <summary>Un fichier prive est fiable au repos : tout ce qu'exige
    /// <see cref="EnsureTrustedFile"/>, et aucun principal non-admin ne detient de droit de lecture.
    /// Fail-closed.</summary>
    public static Result EnsurePrivateFile(string path)
    {
        var trusted = EnsureTrustedFile(path);
        if (!trusted.IsOk)
            return trusted;
        try
        {
            var security = new FileInfo(path)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return Evaluate(path, security, refuserLectureNonAdmin: true);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SignatureInvalid,
                $"Private file '{path}' could not be inspected: {ex.Message}");
        }
    }

    // Les droits de lecture du contenu ; GENERIC_READ / GENERIC_ALL pour la meme raison que plus haut.
    private const FileSystemRights ReadishRights =
        FileSystemRights.ReadData
        | (FileSystemRights)unchecked((int)0x80000000)    // GENERIC_READ
        | (FileSystemRights)0x10000000;                   // GENERIC_ALL

    // ── décision de confiance pure, sans disque (testée avec des entrées synthétiques) ──────────────

    /// <summary>Une entrée de contrôle d'accès aplatie pour l'évaluateur pur.</summary>
    internal readonly record struct AceView(
        SecurityIdentifier Sid, FileSystemRights Rights, AccessControlType Type);

    /// <summary>Lit propriétaire + ACE sur un vrai descripteur de sécurité et évalue la confiance.</summary>
    internal static Result Evaluate(string label, FileSystemSecurity security, bool refuserLectureNonAdmin = false)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        var rules = security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var aces = new List<AceView>(rules.Count);
        foreach (FileSystemAccessRule rule in rules)
            aces.Add(new AceView(
                (SecurityIdentifier)rule.IdentityReference, rule.FileSystemRights, rule.AccessControlType));

        return Evaluate(label, owner, aces, refuserLectureNonAdmin);
    }

    /// <summary>La décision pure : fiable si et seulement si le propriétaire est Administrateurs/SYSTEM
    /// et qu'aucune ACE Allow n'accorde de droit d'écriture ou de contrôle à un principal non admin. Avec
    /// <paramref name="refuserLectureNonAdmin"/> (un fichier privé), un droit de lecture non admin est refusé aussi.</summary>
    internal static Result Evaluate(string label, SecurityIdentifier? owner, IReadOnlyList<AceView> aces,
        bool refuserLectureNonAdmin = false)
    {
        var interdits = refuserLectureNonAdmin ? WriteishRights | ReadishRights : WriteishRights;
        if (owner is null || !IsTrustedPrincipal(owner))
            return Result.Fail(ErrorCodes.SignatureInvalid,
                $"'{label}' is not owned by Administrators/SYSTEM (owner: {owner?.Value ?? "unknown"}).");

        foreach (var ace in aces)
        {
            if (ace.Type != AccessControlType.Allow)
                continue;
            if ((ace.Rights & interdits) == 0)
                continue;
            if (!IsTrustedPrincipal(ace.Sid))
                return Result.Fail(ErrorCodes.SignatureInvalid, refuserLectureNonAdmin
                    ? $"'{label}' grants read, write or control to a non-admin principal ({ace.Sid.Value})."
                    : $"'{label}' grants write/control to a non-admin principal ({ace.Sid.Value}).");
        }

        return Result.Ok();
    }
}
