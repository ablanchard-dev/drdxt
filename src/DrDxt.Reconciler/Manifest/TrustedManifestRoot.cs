using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// TrustedManifestRoot — AT-REST integrity of the SYSTEM-trusted manifest root
// (BLOCKER #1 of the M5 adversarial review).
//
// The boot reconciler (LocalSystem, moat Option B) trusts two files under
//   %ProgramData%\DrDxt
//   * manifest-public.key   — the verify-only root of trust (forge the key → forge the
//     manifest the boot reconciler reapplies), and
//   * desired-state.manifest — the signed desired state.
// The asymmetric signature is the integrity AUTHORITY, but the public key file is the
// root that authority hangs from: if a non-admin can replace manifest-public.key (because
// the directory was created user-owned/user-writable), they can sign a forged manifest
// with their own key and the host would accept it. So we also enforce the at-rest ACL
// posture before trusting either file.
//
// Two complementary guarantees:
//   (CREATE) BuildHardenedSecurity / EnsureCreatedHardened pin the root's OWNER to
//     Administrators (not the creating user). An object's owner holds an implicit
//     WRITE_DAC, so a user-owned root could rewrite its own DACL back to user-writable;
//     pinning the owner to Administrators closes that. Setting owner=Administrators
//     requires an elevated/SYSTEM token, so the hardened root is STRUCTURALLY creatable
//     only by elevated code — a non-elevated writer fails clean and never leaves a
//     user-owned trust root behind. When the root already exists, EnsureCreatedHardened
//     does not no-op: it asserts the at-rest posture and, if a non-admin pre-squatted the
//     root, REPAIRS it (RepairToTrusted: take ownership → re-pin admin-only DACL) from an
//     elevated/SYSTEM context, or FAILS LOUD if it cannot — it never returns Ok on an
//     untrusted existing root (F1). WriteTrustedFile / BuildHardenedFileSecurity extend the
//     same owner-pin to the FILES (manifest-public.key, the signed manifest): a file an
//     admin USER (not SYSTEM) writes would otherwise be owned by that user's S-1-5-21 SID
//     and rejected on a LEGITIMATE root (F3).
//   (VERIFY) EnsureTrustedDirectory / EnsureTrustedFile assert, before trusting a file,
//     that it is not a reparse point (junction/symlink — the OS would follow it to
//     attacker-controlled storage, F2), that its owner is Administrators/SYSTEM, and that
//     no non-admin principal holds a write/control grant. FAIL-CLOSED: any doubt (missing,
//     reparse, unreadable ACL, foreign owner, a user-writable ACE) → Fail, and the caller
//     treats it as no provisioned key / a tampered manifest and reapplies nothing.
//
// TESTABILITY: the trust decision is a pure function (Evaluate over an owner SID + a list
// of ACEs) so the logic is asserted with synthetic inputs — no admin, no real FS — while
// the FS-reading wrappers are exercised against a temp dir whose owner is forced to the
// current (non-admin) user.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class TrustedManifestRoot
{
    // The principals allowed to own the root and to hold a write/control grant on it.
    // Deliberately just Administrators + SYSTEM — the root is created by elevated code
    // with owner=Administrators, so nothing else legitimately writes it.
    private static bool IsTrustedPrincipal(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    // The rights that constitute a "write / control" grant over the object. Any of these
    // held by a non-admin principal makes the root untrusted (a non-admin could replace
    // the key / manifest, or rewrite the DACL/owner).
    private const FileSystemRights WriteishRights =
        FileSystemRights.WriteData                     // CreateFiles
        | FileSystemRights.AppendData                  // CreateDirectories
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions           // WRITE_DAC
        | FileSystemRights.TakeOwnership               // WRITE_OWNER
        // GENERIC rights: an ACE can carry the raw generic mask (GENERIC_WRITE / GENERIC_ALL)
        // instead of the mapped specific bits — those bits do not overlap the specific rights
        // above, so a non-admin ACE granting GENERIC_WRITE/GENERIC_ALL would slip through as
        // "no write grant", the exact grant this guard exists to refuse. Measured 26/08/2026
        // (red team): Evaluate trusted a non-admin holding 0x40000000 / 0x10000000.
        // GENERIC_READ (0x80000000) / GENERIC_EXECUTE (0x20000000) are read-only ⇒ not flagged.
        | (FileSystemRights)0x40000000                 // GENERIC_WRITE
        | (FileSystemRights)0x10000000;                // GENERIC_ALL

    /// <summary>The hardened directory security for the manifest root: owner pinned to
    /// Administrators, a PROTECTED admin-write DACL (FullControl for Administrators +
    /// SYSTEM, ReadAndExecute for authenticated users, no inheritance from a looser
    /// parent). Setting owner=Administrators requires an elevated/SYSTEM token, so this
    /// descriptor is creatable only by elevated code (a standard user token cannot apply
    /// it — the create fails clean instead of leaving a user-owned root).</summary>
    public static DirectorySecurity BuildHardenedSecurity()
    {
        var security = new DirectorySecurity();
        // Stop inheriting a (potentially user-writable) parent ACL.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Owner = Administrators (not the creating user) so the creator cannot later use
        // the owner's implicit WRITE_DAC to reopen the DACL.
        security.SetOwner(admins);

        const InheritanceFlags inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Read-only for everyone else — the App / a low-priv reader can read while non-admin.
        security.AddAccessRule(new FileSystemAccessRule(
            authUsers, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        return security;
    }

    /// <summary>The hardened FILE security for a trusted file (manifest-public.key / the
    /// signed manifest): owner pinned to Administrators, a PROTECTED admin-only DACL
    /// (FullControl for Administrators + SYSTEM, ReadAndExecute for authenticated users so a
    /// low-priv reader can still read, no inheritance). Pinning the FILE owner closes
    /// BLOCKER #1 / F3: a file written by an admin USER (typical installer) would otherwise
    /// be owned by that user's S-1-5-21 SID and the SYSTEM reconciler would reject the
    /// LEGITIMATE root. Setting owner=Administrators needs an elevated/SYSTEM token, so this
    /// descriptor is applicable only by elevated code.</summary>
    public static FileSecurity BuildHardenedFileSecurity()
    {
        var security = new FileSecurity();
        // Stop inheriting a (potentially user-writable) parent ACL.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        // Owner = Administrators (not the writing user) so a non-SYSTEM admin installer
        // cannot leave a user-owned file the SYSTEM reconciler would silently reject.
        security.SetOwner(admins);

        // Files carry no inheritance flags (a file is a leaf — applying them throws).
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            authUsers, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        return security;
    }

    /// <summary>11/09/2026 — la securite d'un fichier prive (la cle de signature de la machine) :
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

    /// <summary>Creates the hardened root if missing; if it already exists, asserts it is
    /// trusted at rest and REPAIRS it (take ownership → pin owner=Administrators → apply the
    /// protected, inheritance-stripped admin-only DACL) when it is not. Fail-CLEAN (never
    /// throws). Never returns Ok on an untrusted existing root (BLOCKER #1 / F1): a
    /// pre-squatted user-owned <c>%ProgramData%\DrDxt</c> is repaired from an elevated/SYSTEM
    /// context, or — when the caller is not elevated and cannot take ownership / set
    /// owner=Administrators — this returns Fail. A reparse point (junction/symlink) is never
    /// created over, repaired, or trusted (F2). By construction no user-owned trust root is
    /// ever created or left behind (BLOCKER #1c).</summary>
    public static Result EnsureCreatedHardened(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // A reparse point could redirect the root to attacker-controlled storage —
                // never repair/trust one; reject outright (F2).
                if (IsReparsePoint(dir))
                    return Result.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest root '{dir}' is a reparse point (junction/symlink): refusing to trust or repair.");

                // Already trusted at rest → nothing to do.
                var trust = EnsureTrustedDirectory(dir);
                if (trust.IsOk)
                    return Result.Ok();

                // Untrusted existing root → REPAIR it. Requires elevation/SYSTEM (take
                // ownership + re-pin owner=Administrators); a non-elevated caller cannot and
                // this fails loudly — it never returns Ok on an untrusted root.
                var repaired = RepairToTrusted(dir);
                if (!repaired.IsOk)
                    return repaired;

                // Re-verify: the repair must have produced a trusted posture, else fail.
                return EnsureTrustedDirectory(dir);
            }

            new DirectoryInfo(dir).Create(BuildHardenedSecurity());
            // Verify the create actually produced an admin-owned root (a non-elevated token
            // cannot set owner=Administrators, so Create throws above; this re-asserts the
            // posture for defence-in-depth and never returns Ok on an untrusted result).
            return EnsureTrustedDirectory(dir);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not create or harden the manifest root '{dir}': {ex.Message}");
        }
    }

    /// <summary>Repairs an existing directory to the trusted posture: takes ownership
    /// (owner=Administrators) then applies the protected, inheritance-stripped admin-only
    /// DACL. Setting owner=Administrators succeeds only for an elevated/SYSTEM token (whose
    /// Administrators SID carries SE_GROUP_OWNER); a non-elevated token (Administrators
    /// deny-only) cannot and this returns Fail — fail-closed, never silently succeeding on
    /// an untrusted root. Fail-CLEAN (never throws).</summary>
    public static Result RepairToTrusted(string dir)
    {
        try
        {
            if (IsReparsePoint(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"'{dir}' is a reparse point (junction/symlink): refusing to repair.");

            var di = new DirectoryInfo(dir);

            // Phase 1: TAKE OWNERSHIP — owner = Administrators. Only an elevated/SYSTEM token
            // can; a non-elevated one throws here (→ Fail), so the moat is never enabled on a
            // root a non-admin still controls.
            var takeOwner = new DirectorySecurity();
            takeOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            di.SetAccessControl(takeOwner);

            // Phase 2: now the owner (⇒ implicit WRITE_DAC) — strip inheritance and pin the
            // protected admin-only DACL, evicting any non-admin write/control grant.
            di.SetAccessControl(BuildHardenedSecurity());
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure,
                $"Could not repair the manifest root '{dir}' to admin-owned (elevation required): {ex.Message}");
        }
    }

    /// <summary>Atomically writes a TRUSTED file (the signed manifest / manifest-public.key)
    /// and PINS its owner=Administrators + a protected admin-only DACL, so the SYSTEM
    /// reconciler trusts it at rest (BLOCKER #1 / F3). Ensures/repairs the parent root first.
    /// FAIL-LOUD: if the owner cannot be set (caller not elevated) the partial write is
    /// removed and this returns Fail — it never leaves a user-owned file the SYSTEM service
    /// would silently reject. Fail-CLEAN (never throws).</summary>
    /// <param name="prive">11/09/2026 — un fichier prive (la cle de signature de la machine) : cree
    /// DIRECTEMENT avec <see cref="BuildPrivateFileSecurity"/>, sans jamais heriter de la lecture des
    /// utilisateurs du dossier, puis verifie par <see cref="EnsurePrivateFile"/>. Sans cela, entre la
    /// creation du temporaire et la pose de son ACL, un processus local aurait pu l'ouvrir en lecture —
    /// et le blob DPAPI « machine » se dechiffre depuis n'importe quel compte local.</param>
    public static Result WriteTrustedFile(string path, byte[] bytes, bool prive = false)
    {
        var tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return Result.Fail(ErrorCodes.IoFailure,
                    $"Trusted file '{path}' has no parent directory.");

            // The root must be admin-owned before we drop a trusted file into it.
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
                File.WriteAllBytes(tmp, bytes);
                // Pin owner + protected DACL on the temp file before publishing it under the real
                // name, so the trusted file is never momentarily user-owned.
                new FileInfo(tmp).SetAccessControl(BuildHardenedFileSecurity());
            }
            // File.Move is a rename that preserves the security descriptor.
            File.Move(tmp, path, overwrite: true);

            // Defence-in-depth: the published file must be admin-trusted at rest, else fail.
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

    /// <summary>True iff the path is a reparse point (junction/symlink). Throws if the
    /// attributes are unreadable — callers run this inside a fail-closed try/catch.</summary>
    internal static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Asserts a directory is trusted at rest: it exists, its OWNER is
    /// Administrators/SYSTEM, and NO non-admin principal holds a write/control grant.
    /// FAIL-CLOSED on any doubt.</summary>
    public static Result EnsureTrustedDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted manifest root '{dir}' does not exist.");

            // A reparse point (junction/symlink) is followed silently by the OS, so the SD we
            // would read is the TARGET's, not the root's — reject it before trusting (F2).
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

    /// <summary>Asserts a file is trusted at rest: its OWNER is Administrators/SYSTEM AND
    /// the directory that holds it is itself trusted (so a non-admin can neither rewrite
    /// the file in place nor swap it by recreating it in a writable directory).
    /// FAIL-CLOSED on any doubt.</summary>
    public static Result EnsureTrustedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Result.Fail(ErrorCodes.SignatureInvalid,
                    $"Trusted file '{path}' does not exist.");

            // Reject a reparse-point FILE (symlink) before trusting it — the OS would follow
            // it to a target whose SD/bytes the attacker controls (F2).
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

    /// <summary>11/09/2026 — un fichier prive est fiable au repos : tout ce qu'exige
    /// <see cref="EnsureTrustedFile"/>, et aucun principal non-admin ne detient de droit de lecture.
    /// FAIL-CLOSED.</summary>
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

    // ── pure, FS-free trust decision (unit-tested with synthetic inputs) ──────────────

    /// <summary>A flattened access-control entry for the pure evaluator.</summary>
    internal readonly record struct AceView(
        SecurityIdentifier Sid, FileSystemRights Rights, AccessControlType Type);

    /// <summary>Reads owner + ACEs off a real security descriptor and evaluates trust.</summary>
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

    /// <summary>The pure decision: trusted iff the owner is Administrators/SYSTEM and no
    /// Allow ACE grants a write/control right to a non-admin principal. With
    /// <paramref name="refuserLectureNonAdmin"/> (a PRIVATE file), a non-admin read grant is refused too.</summary>
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
