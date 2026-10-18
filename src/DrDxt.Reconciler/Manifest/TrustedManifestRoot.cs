using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// TrustedManifestRoot : intégrité au repos de la racine du manifeste à laquelle SYSTEM fait
// confiance (trou trouvé par une revue adverse).
//
// Le réconciliateur de démarrage (LocalSystem, moat option B) fait confiance à deux fichiers sous
//   %ProgramData%\DrDxt
//   * manifest-public.key   : la racine de confiance en vérification seule (falsifier la clé →
//     falsifier le manifeste que le réconciliateur de démarrage réapplique), et
//   * desired-state.manifest : l'état voulu signé.
// La signature asymétrique est l'autorité d'intégrité, mais le fichier de clé publique est la
// racine à laquelle cette autorité est accrochée : si un non-admin peut remplacer
// manifest-public.key (parce que le dossier a été créé possédé ou inscriptible par l'utilisateur),
// il peut signer un manifeste falsifié avec sa propre clé et l'hôte l'accepterait. On impose donc
// aussi la protection ACL au repos avant de faire confiance à l'un ou l'autre fichier.
//
// Deux garanties complémentaires :
//   (création) BuildHardenedSecurity / EnsureCreatedHardened épinglent le propriétaire de la
//     racine à Administrateurs (pas l'utilisateur créateur). Le propriétaire d'un objet détient un
//     WRITE_DAC implicite : une racine possédée par l'utilisateur pourrait réécrire sa propre DACL
//     pour redevenir inscriptible ; épingler le propriétaire à Administrateurs ferme ce trou. Poser
//     owner=Administrators exige un jeton élevé / SYSTEM : la racine durcie ne peut donc, par
//     construction, être créée que par du code élevé ; un écrivain non élevé échoue proprement et
//     ne laisse jamais de racine de confiance possédée par l'utilisateur. Si la racine existe déjà,
//     EnsureCreatedHardened ne passe pas son tour : il vérifie la protection au repos et, si un
//     non-admin a squatté la racine d'avance, la répare (RepairToTrusted : prise de possession →
//     réépinglage de la DACL réservée aux admins) depuis un contexte élevé / SYSTEM, ou échoue
//     bruyamment s'il ne le peut pas ; il ne rend jamais Ok sur une racine existante non fiable.
//     WriteTrustedFile / BuildHardenedFileSecurity étendent le même épinglage du propriétaire aux
//     fichiers (manifest-public.key, le manifeste signé) : sinon un fichier écrit par un
//     utilisateur admin (pas SYSTEM) appartiendrait au SID S-1-5-21 de cet utilisateur et serait
//     rejeté sur une racine légitime.
//   (vérification) EnsureTrustedDirectory / EnsureTrustedFile vérifient, avant de faire confiance
//     à un fichier, qu'il n'est pas un point de reparse (jonction / lien symbolique : l'OS le
//     suivrait vers un stockage contrôlé par l'attaquant), que son propriétaire est
//     Administrateurs/SYSTEM, et qu'aucun principal non admin ne détient de droit d'écriture ou de
//     contrôle. Fail-closed : au moindre doute (absent, reparse, ACL illisible, propriétaire
//     étranger, ACE inscriptible par l'utilisateur) → Fail, et l'appelant le traite comme une clé
//     non fournie / un manifeste altéré et ne réapplique rien.
//
// Testabilité : la décision de confiance est une fonction pure (Evaluate sur un SID de
// propriétaire + une liste d'ACE), vérifiée avec des entrées synthétiques (sans admin, sans vrai
// système de fichiers) ; les enveloppes qui lisent le disque sont exercées sur un dossier
// temporaire dont le propriétaire est forcé à l'utilisateur courant (non admin).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class TrustedManifestRoot
{
    // Les principaux admis à posséder la racine et à y détenir un droit d'écriture ou de contrôle.
    // Exprès seulement Administrateurs + SYSTEM : la racine est créée par du code élevé avec
    // owner=Administrators, rien d'autre ne l'écrit légitimement.
    private static bool IsTrustedPrincipal(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    // Les droits qui forment un droit « d'écriture / de contrôle » sur l'objet. L'un d'eux détenu
    // par un principal non admin rend la racine non fiable (un non-admin pourrait remplacer la clé /
    // le manifeste, ou réécrire la DACL / le propriétaire).
    private const FileSystemRights WriteishRights =
        FileSystemRights.WriteData                     // CreateFiles
        | FileSystemRights.AppendData                  // CreateDirectories
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions           // WRITE_DAC
        | FileSystemRights.TakeOwnership               // WRITE_OWNER
        // Droits génériques : une ACE peut porter le masque générique brut (GENERIC_WRITE / GENERIC_ALL)
        // au lieu des bits spécifiques correspondants ; ces bits ne recoupent pas les droits spécifiques
        // ci-dessus, et une ACE non admin accordant GENERIC_WRITE/GENERIC_ALL passerait pour « aucun droit
        // d'écriture », exactement le droit que cette garde doit refuser. Mesuré le 26/08/2026 (red team) :
        // sans ces bits, Evaluate faisait confiance à un non-admin détenant 0x40000000 / 0x10000000.
        // GENERIC_READ (0x80000000) / GENERIC_EXECUTE (0x20000000) sont en lecture seule ⇒ non signalés.
        | (FileSystemRights)0x40000000                 // GENERIC_WRITE
        | (FileSystemRights)0x10000000;                // GENERIC_ALL

    /// <summary>La sécurité durcie du dossier racine du manifeste : propriétaire épinglé à
    /// Administrateurs, DACL protégée en écriture admin (FullControl pour Administrateurs + SYSTEM,
    /// ReadAndExecute pour les utilisateurs authentifiés, aucun héritage d'un parent plus lâche).
    /// Poser owner=Administrators exige un jeton élevé / SYSTEM : ce descripteur ne se crée que par
    /// du code élevé (un jeton d'utilisateur standard ne peut pas l'appliquer : la création échoue
    /// proprement au lieu de laisser une racine possédée par l'utilisateur).</summary>
    public static DirectorySecurity BuildHardenedSecurity()
    {
        var security = new DirectorySecurity();
        // Cesser d'hériter d'une ACL parente (peut-être inscriptible par l'utilisateur).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Propriétaire = Administrateurs (pas l'utilisateur créateur) pour que le créateur ne puisse
        // pas ensuite se servir du WRITE_DAC implicite du propriétaire pour rouvrir la DACL.
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

    /// <summary>La sécurité durcie d'un fichier de confiance (manifest-public.key / le manifeste
    /// signé) : propriétaire épinglé à Administrateurs, DACL protégée réservée aux admins
    /// (FullControl pour Administrateurs + SYSTEM, ReadAndExecute pour les utilisateurs authentifiés
    /// afin qu'un lecteur peu privilégié puisse lire, aucun héritage). Sans épinglage du propriétaire
    /// du fichier, un fichier écrit par un utilisateur admin (installeur typique) appartiendrait au SID
    /// S-1-5-21 de cet utilisateur et le réconciliateur SYSTEM rejetterait la racine légitime. Poser
    /// owner=Administrators exige un jeton élevé / SYSTEM : ce descripteur ne s'applique que par du
    /// code élevé.</summary>
    public static FileSecurity BuildHardenedFileSecurity()
    {
        var security = new FileSecurity();
        // Cesser d'hériter d'une ACL parente (peut-être inscriptible par l'utilisateur).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Propriétaire = Administrateurs (pas l'utilisateur qui écrit) pour qu'un installeur admin non
        // SYSTEM ne laisse pas un fichier possédé par l'utilisateur, que le réconciliateur SYSTEM rejetterait en silence.
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

    /// <summary>La securite d'un fichier prive (la cle de signature de la machine) :
    /// proprietaire Administrateurs, DACL protegee, Administrateurs + SYSTEM seulement. Aucune lecture
    /// pour les utilisateurs authentifies, contrairement aux fichiers publics : la protection DPAPI
    /// « machine » de la cle se dechiffre depuis n'importe quel compte local, c'est donc cette ACL qui
    /// garde la cle, pas le chiffrement.</summary>
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

    /// <summary>Crée la racine durcie si elle manque ; si elle existe déjà, vérifie qu'elle est
    /// fiable au repos et la répare sinon (prise de possession → owner=Administrators épinglé →
    /// DACL protégée, sans héritage, réservée aux admins). Échec propre (ne lève jamais). Ne rend
    /// jamais Ok sur une racine existante non fiable : un <c>%ProgramData%\DrDxt</c> squatté d'avance
    /// par l'utilisateur est réparé depuis un contexte élevé / SYSTEM ou, si l'appelant n'est pas
    /// élevé et ne peut ni prendre possession ni poser owner=Administrators, la méthode rend Fail.
    /// Un point de reparse (jonction / lien symbolique) n'est jamais recouvert, réparé ni tenu pour
    /// fiable. Par construction, aucune racine de confiance possédée par l'utilisateur n'est jamais
    /// créée ni laissée (BLOCKER #1c).</summary>
    public static Result EnsureCreatedHardened(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // Un point de reparse pourrait rediriger la racine vers un stockage contrôlé par l'attaquant :
                // ne jamais le réparer ni lui faire confiance ; rejet immédiat.
                if (IsReparsePoint(dir))
                    return Result.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest root '{dir}' is a reparse point (junction/symlink): refusing to trust or repair.");

                // Déjà fiable au repos → rien à faire.
                var trust = EnsureTrustedDirectory(dir);
                if (trust.IsOk)
                    return Result.Ok();

                // Racine existante non fiable → la réparer. Exige l'élévation / SYSTEM (prise de possession +
                // réépinglage owner=Administrators) ; un appelant non élevé ne le peut pas et la méthode échoue
                // bruyamment : elle ne rend jamais Ok sur une racine non fiable.
                var repaired = RepairToTrusted(dir);
                if (!repaired.IsOk)
                    return repaired;

                // Revérifier : la réparation doit avoir produit une protection fiable, sinon échec.
                return EnsureTrustedDirectory(dir);
            }

            new DirectoryInfo(dir).Create(BuildHardenedSecurity());
            // Vérifier que la création a bien produit une racine possédée par les admins (un jeton non
            // élevé ne peut pas poser owner=Administrators, Create lève donc plus haut ; ceci revérifie la
            // protection en défense en profondeur et ne rend jamais Ok sur un résultat non fiable).
            return EnsureTrustedDirectory(dir);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not create or harden the manifest root '{dir}': {ex.Message}");
        }
    }

    /// <summary>Remet un dossier existant dans la protection fiable : prend possession
    /// (owner=Administrators) puis applique la DACL protégée, sans héritage, réservée aux admins.
    /// Poser owner=Administrators ne réussit qu'avec un jeton élevé / SYSTEM (dont le SID
    /// Administrateurs porte SE_GROUP_OWNER) ; un jeton non élevé (Administrateurs en deny-only) ne le
    /// peut pas et la méthode rend Fail : fail-closed, jamais de réussite silencieuse sur une racine
    /// non fiable. Échec propre (ne lève jamais).</summary>
    public static Result RepairToTrusted(string dir)
    {
        try
        {
            if (IsReparsePoint(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"'{dir}' is a reparse point (junction/symlink): refusing to repair.");

            var di = new DirectoryInfo(dir);

            // Étape 1 : prise de possession, owner = Administrators. Seul un jeton élevé / SYSTEM le peut ;
            // un jeton non élevé lève ici (→ Fail) : le moat n'est jamais activé sur une racine qu'un
            // non-admin contrôle encore.
            var takeOwner = new DirectorySecurity();
            takeOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            di.SetAccessControl(takeOwner);

            // Étape 2 : désormais propriétaire (⇒ WRITE_DAC implicite), retirer l'héritage et épingler la
            // DACL protégée réservée aux admins, en chassant tout droit d'écriture / de contrôle non admin.
            di.SetAccessControl(BuildHardenedSecurity());
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not repair the manifest root '{dir}' to admin-owned (elevation required): {ex.Message}");
        }
    }

    /// <summary>Écrit atomiquement un fichier de confiance (le manifeste signé / manifest-public.key)
    /// et épingle owner=Administrators + une DACL protégée réservée aux admins, pour que le
    /// réconciliateur SYSTEM lui fasse confiance au repos. S'assure d'abord de la racine parente (ou
    /// la répare). Échec bruyant : si le propriétaire ne peut pas être posé (appelant non élevé),
    /// l'écriture partielle est retirée et la méthode rend Fail ; elle ne laisse jamais un fichier
    /// possédé par l'utilisateur, que le service SYSTEM rejetterait en silence. Échec propre (ne lève jamais).</summary>
    /// <param name="prive">Un fichier prive (la cle de signature de la machine) : cree
    /// directement avec <see cref="BuildPrivateFileSecurity"/>, sans jamais heriter de la lecture des
    /// utilisateurs du dossier, puis verifie par <see cref="EnsurePrivateFile"/>. Sans cela, entre la
    /// creation du temporaire et la pose de son ACL, un processus local pourrait l'ouvrir en lecture,
    /// et le blob DPAPI « machine » se dechiffre depuis n'importe quel compte local.</param>
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
            }
            else
            {
                using (var flux = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    flux.Write(bytes);
                // Épingler propriétaire + DACL protégée sur le fichier temporaire avant de le publier sous son
                // vrai nom : le fichier de confiance n'appartient jamais, même un instant, à l'utilisateur.
                new FileInfo(tmp).SetAccessControl(BuildHardenedFileSecurity());
            }
            // File.Move est un renommage qui garde le descripteur de sécurité.
            File.Move(tmp, path, overwrite: true);

            // Défense en profondeur : le fichier publié doit être fiable au repos (côté admin), sinon échec.
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

    /// <summary>Vrai si et seulement si le chemin est un point de reparse (jonction / lien
    /// symbolique). Lève si les attributs sont illisibles : les appelants l'appellent dans un
    /// try/catch fail-closed.</summary>
    internal static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Vérifie qu'un dossier est fiable au repos : il existe, son propriétaire est
    /// Administrateurs/SYSTEM, et aucun principal non admin ne détient de droit d'écriture ou de
    /// contrôle. Fail-closed au moindre doute.</summary>
    public static Result EnsureTrustedDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted manifest root '{dir}' does not exist.");

            // Un point de reparse (jonction / lien symbolique) est suivi en silence par l'OS : le SD lu
            // serait celui de la cible, pas de la racine ; le rejeter avant de faire confiance.
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

    /// <summary>Vérifie qu'un fichier est fiable au repos : son propriétaire est
    /// Administrateurs/SYSTEM et le dossier qui le contient est lui-même fiable (un non-admin ne peut
    /// ni réécrire le fichier sur place ni l'échanger en le recréant dans un dossier inscriptible).
    /// Fail-closed au moindre doute.</summary>
    public static Result EnsureTrustedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted file '{path}' does not exist.");

            // Rejeter un fichier point de reparse (lien symbolique) avant de lui faire confiance : l'OS le
            // suivrait vers une cible dont l'attaquant contrôle le SD et les octets.
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

    // Les droits qui permettent de lire le contenu d'un fichier. GENERIC_READ / GENERIC_ALL pour la meme
    // raison que plus haut : un ACE peut porter le masque generique brut au lieu des bits specifiques.
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
