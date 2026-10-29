using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>Le processus qui a demandé une écriture. Son PID et l'heure de création de ce processus le désignent sans
/// ambiguïté : un PID réutilisé par un autre processus n'a pas la même heure de création.</summary>
public sealed record AppelantDuJournal(string Sid, int Pid, DateTimeOffset CreationDuProcessus);

/// <summary>Une écriture privilégiée faite, ou en cours, dont l'origine n'est pas encore notée par l'App.</summary>
public sealed record EntreeDuJournal(Guid Id, PrivilegedOp Op, string? ImageAvant, AppelantDuJournal Appelant, DateTimeOffset Le);

/// <summary>
/// Le journal d'écriture anticipée : avant chaque écriture privilégiée journalisée, ce qu'il faut pour la rendre. L'App
/// l'acquitte une fois l'origine notée chez elle ; une entrée restée là est une écriture dont le demandeur est mort avant.
/// Chaque modification relit et réécrit le fichier entier, sur le disque avant de rendre la main (FichierJsonAtomique).
/// Un fichier illisible n'est jamais lu vide ni écrasé : il garderait des origines qu'on perdrait.
///
/// Le dossier du fichier est réservé au journal : son accès est réécrit à chaque écriture, SYSTEM et Administrateurs
/// seulement, sans héritage. Le helper élevé et le service SYSTEM écrivent le même fichier ; le verrou est un fichier ouvert
/// sans partage dans ce dossier, qu'aucun processus du compte ne peut ouvrir, donc ni tenir ni bloquer.
///
/// Ordre de construction obligatoire : le dossier du journal naît dans une racine déjà réparée
/// (ProgramDataManifestStorage, qui refuse les points d'analyse et ferme l'écriture à Users). Seul le dernier dossier est
/// contrôlé ici : un dossier créé d'avance par un compte qui y tiendrait le verrou rendrait le journal occupé pour
/// toujours, la réparation n'ayant lieu que verrou pris.
/// </summary>
/// <param name="maxParAppelant">Les entrées en attente d'un même appelant, au plus ; au-delà, sa pose est refusée (un
/// appelant qui n'acquitte jamais ferait grossir sans fin un journal réécrit à chaque écriture).</param>
/// <param name="duCompte">Le journal du compte (<see cref="DuCompte"/>) : mêmes entrées, mêmes règles, pour les seules valeurs de
/// la session (HKCU), que l'App écrit avec son jeton.</param>
public sealed class JournalDEcritureAnticipee(string chemin, TimeSpan? attente = null, int maxParAppelant = JournalDEcritureAnticipee.MaxParAppelantParDefaut,
    bool duCompte = false)
{
    /// <summary>
    /// Le journal des valeurs de la session, dans le dossier du compte. Le compte peut le réécrire sans rien y gagner : une
    /// entrée ne rend qu'une valeur de sa propre ruche (HKCU), la seule op qu'il porte (une autre lue n'est ni rendue ni
    /// recopiée). Verrou : un mutex de la session ; écriture : FichierJsonAtomique.
    /// </summary>
    public static JournalDEcritureAnticipee DuCompte(string chemin, TimeSpan? attente = null) => new(chemin, attente, duCompte: true);

    /// <summary>Une valeur de la session de l'utilisateur, la seule op que porte le journal du compte.</summary>
    public static bool DeLaSession(PrivilegedOp op)
        => op is SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } or DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser };

    public const int MaxParAppelantParDefaut = 512;

    public const string CodeErreur = "JEA_JOURNAL";
    /// <summary>Le journal ne se lit plus : jamais écrasé (une origine s'y perdrait), rien ne s'écrit tant qu'il l'est. Pas
    /// un refus passager.</summary>
    public const string CodeIllisible = "JEA_ILLISIBLE";
    /// <summary>Aucune entrée de cet appelant ne porte cette op (rendue par un rejeu, ou journal réécrit) : définitif, l'App
    /// ne reprend pas cet acquit.</summary>
    public const string CodeSansEntree = "JEA_ACQUIT_SANS_ENTREE";
    private readonly TimeSpan _attente = attente ?? TimeSpan.FromSeconds(10);

    private readonly FichierJsonAtomique _fichier = new(chemin, CodeErreur);
    private readonly string _chemin = chemin;

    private sealed record Ligne(Guid Id, string Op, string? ImageAvant, string Sid, int Pid, DateTimeOffset CreationDuProcessus,
        DateTimeOffset Le);

    public Result<EntreeDuJournal> Ajouter(PrivilegedOp op, string? imageAvant, AppelantDuJournal appelant, DateTimeOffset le)
    {
        if (duCompte && !DeLaSession(op))
            return Result<EntreeDuJournal>.Fail(CodeErreur, "The account journal only carries the session's own values.");
        var json = PrivilegedOpCodec.Serialize(op);
        if (!json.IsOk) return Result<EntreeDuJournal>.Fail(CodeErreur, json.Error?.Message ?? "op not serializable");
        var ligne = new Ligne(Guid.NewGuid(), json.Value!, imageAvant, appelant.Sid, appelant.Pid, appelant.CreationDuProcessus, le);
        var r = SousVerrou(lignes =>
        {
            if (lignes.Count(l => l.Sid == appelant.Sid && l.Pid == appelant.Pid && l.CreationDuProcessus == appelant.CreationDuProcessus) >= maxParAppelant)
                return Result.Fail(CodeErreur, "Too many pending writes for this caller.");
            lignes.Add(ligne);
            return Result.Ok();
        });
        return r.IsOk
            ? Result<EntreeDuJournal>.Ok(new EntreeDuJournal(ligne.Id, op, imageAvant, appelant, le))
            : Result<EntreeDuJournal>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
    }

    /// <summary>L'App a noté l'origine : la plus ancienne entrée de cet appelant qui porte cette op sort du journal.</summary>
    public Result Acquitter(PrivilegedOp op, AppelantDuJournal appelant)
    {
        var json = PrivilegedOpCodec.Serialize(op);
        if (!json.IsOk) return Result.Fail(CodeErreur, json.Error?.Message ?? "op not serializable");
        return SousVerrou(lignes =>
        {
            var i = lignes.FindIndex(l => l.Op == json.Value && l.Sid == appelant.Sid && l.Pid == appelant.Pid
                                          && l.CreationDuProcessus == appelant.CreationDuProcessus);
            if (i < 0) return Result.Fail(CodeSansEntree, "No pending write of this caller carries this op.");
            lignes.RemoveAt(i);
            return Result.Ok();
        });
    }

    /// <summary>L'écriture a échoué : rien n'a changé, il n'y a rien à rendre.</summary>
    public Result Retirer(Guid id)
        => SousVerrou(lignes => lignes.RemoveAll(l => l.Id == id) > 0
            ? Result.Ok()
            : Result.Fail(CodeErreur, "No pending write has this id."));

    public Result<IReadOnlyList<EntreeDuJournal>> EnAttente()
    {
        var lu = Lire();
        if (!lu.IsOk) return Result<IReadOnlyList<EntreeDuJournal>>.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);
        var entrees = new List<EntreeDuJournal>();
        foreach (var l in lu.Value!)
        {
            if (PrivilegedOpCodec.Deserialize(l.Op) is not { IsOk: true, Value: { } op })
                return Result<IReadOnlyList<EntreeDuJournal>>.Fail(CodeIllisible, $"Pending write {l.Id} carries an unreadable op.");
            if (duCompte && !DeLaSession(op)) continue;
            entrees.Add(new EntreeDuJournal(l.Id, op, l.ImageAvant, new AppelantDuJournal(l.Sid, l.Pid, l.CreationDuProcessus), l.Le));
        }
        return Result<IReadOnlyList<EntreeDuJournal>>.Ok(entrees);
    }

    private Result<List<Ligne>> Lire()
    {
        // %ProgramData% laisse tout compte créer un dossier, la réparation de la racine ne descend pas : un journal non fiable
        // (propriétaire Administrateurs/SYSTEM, aucune écriture pour un autre) n'est pas de DrDXT. Mis de côté, jamais
        // rejoué ni recopié ; impossible à mettre de côté : illisible, rien n'est écrit.
        if (!duCompte && File.Exists(_chemin) && !DrDxt.Reconciler.Manifest.TrustedManifestRoot.EnsureTrustedFile(_chemin).IsOk)
            return _fichier.MettreDeCote() ? Result<List<Ligne>>.Ok([]) : Result<List<Ligne>>.Fail(CodeErreur, "Untrusted journal.");
        var texte = _fichier.Read();
        if (!texte.IsOk) return Result<List<Ligne>>.Fail(texte.Error!.Value.Code, texte.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(texte.Value)) return Result<List<Ligne>>.Ok([]);
        try
        {
            return JsonSerializer.Deserialize<List<Ligne>>(texte.Value) is { } lignes && lignes.TrueForAll(l => l is not null)
                ? Result<List<Ligne>>.Ok(lignes)
                : Result<List<Ligne>>.Fail(CodeIllisible, "Unreadable journal.");
        }
        catch (JsonException ex)
        {
            return Result<List<Ligne>>.Fail(CodeIllisible, $"Unreadable journal: {ex.Message}");
        }
    }

    private Result SousVerrou(Func<List<Ligne>, Result> modifier)
    {
        if (duCompte) return SousVerrouDuCompte(modifier);
        try
        {
            // Le dossier est créé déjà fermé ; un dossier existant n'est jamais cru. Un lien est refusé avant d'y ouvrir quoi
            // que ce soit, puis encore verrou ouvert (il ne peut plus être remplacé). Accès et propriétaire (Administrateurs :
            // un propriétaire garde le droit de rendre l'accès) sont réparés sous le verrou.
            var dossier = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_chemin))!);
            if (!dossier.Exists) dossier.Create(AccesReserve());
            if (EstUnLien(dossier)) return Result.Fail(CodeErreur, "The journal folder is a link.");
            using var verrou = Prendre();
            if (verrou is null) return Result.Fail(CodeErreur, "The journal is busy.");
            if (EstUnLien(dossier)) return Result.Fail(CodeErreur, "The journal folder is a link.");
            dossier.SetAccessControl(AccesReserve());
            var lu = Lire();
            if (!lu.IsOk) return Result.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);
            var r = modifier(lu.Value!);
            // Propriétaire Administrateurs et accès protégé posés sur le temporaire avant le renommage : sous le propriétaire
            // par défaut (le compte qui a élevé), un programme de ce compte sans élévation pourrait en rouvrir l'accès et y
            // forger des écritures que SYSTEM rejouerait.
            return r.IsOk
                ? DrDxt.Reconciler.Manifest.TrustedManifestRoot.WriteTrustedFile(_chemin,
                    System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lu.Value)))
                : r;
        }
        catch (SystemException ex)
        {
            return Result.Fail(CodeErreur, $"Journal not writable: {ex.Message}");
        }
    }

    // Un mutex de la session, nommé d'après le fichier : deux App du même compte ne réécrivent jamais le journal en même temps.
    private Result SousVerrouDuCompte(Func<List<Ligne>, Result> modifier)
    {
        using var verrou = new Mutex(false, @"Local\DrDXT-jea-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_chemin).ToUpperInvariant())))[..32]);
        var pris = false;
        try
        {
            try { pris = verrou.WaitOne(_attente); }
            catch (AbandonedMutexException) { pris = true; /* le détenteur est mort : le fichier, lui, est atomique */ }
            if (!pris) return Result.Fail(CodeErreur, "The journal is busy.");
            var lu = Lire();
            if (!lu.IsOk) return Result.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);
            var r = modifier(lu.Value!);
            return r.IsOk ? _fichier.WriteAtomic(JsonSerializer.Serialize(lu.Value)) : r;
        }
        finally { if (pris) verrou.ReleaseMutex(); }
    }

    // Le verrou naît aux Administrateurs, accès SYSTEM et Administrateurs seulement : sous le propriétaire par défaut, le
    // compte qui a élevé pourrait le tenir ouvert sans élévation et faire refuser toute écriture. Un verrou au propriétaire
    // non fiable est remplacé une fois, sinon « occupé ».
    private FileStream? Prendre()
    {
        var chemin = _chemin + ".verrou";
        var fin = DateTime.UtcNow + _attente;
        var remplace = false;
        while (true)
        {
            try
            {
                var flux = new FileInfo(chemin).Create(FileMode.OpenOrCreate,
                    FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ReadPermissions, FileShare.None, 1,
                    FileOptions.None, VerrouReserve());
                var proprietaire = flux.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (proprietaire is not null && (proprietaire.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                                                 || proprietaire.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                    return flux;
                flux.Dispose();
                if (remplace) return null;
                File.Delete(chemin);
                remplace = true;
            }
            catch (IOException) when (DateTime.UtcNow < fin) { Thread.Sleep(20); }
            catch (IOException) { return null; }
        }
    }

    private static FileSecurity VerrouReserve()
    {
        var acces = new FileSecurity();
        acces.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acces.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acces.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return acces;
    }

    // Les attributs du lien lui-même, sans le suivre.
    private static bool EstUnLien(DirectoryInfo dossier)
    {
        dossier.Refresh();
        return dossier.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static DirectorySecurity AccesReserve()
    {
        var acces = new DirectorySecurity();
        acces.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acces.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acces.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acces;
    }
}
