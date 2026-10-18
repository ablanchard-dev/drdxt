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
/// Dépendance à l'ordre de construction, obligatoire : le dossier du journal doit naître dans une racine déjà réparée
/// (ProgramDataManifestStorage, qui refuse les points d'analyse et ferme l'écriture à Users). Seul le dernier dossier est
/// contrôlé ici ; et un dossier qu'un compte aurait créé d'avance, en y tenant le verrou, rendrait le journal occupé pour
/// toujours, la réparation n'ayant lieu qu'une fois le verrou pris.
/// </summary>
/// <param name="maxParAppelant">Les entrées en attente d'un même appelant, au plus. Au-delà, sa pose est refusée : un appelant
/// qui n'acquitterait jamais ferait grossir sans fin un journal relu et réécrit à chaque écriture.</param>
public sealed class JournalDEcritureAnticipee(string chemin, TimeSpan? attente = null, int maxParAppelant = JournalDEcritureAnticipee.MaxParAppelantParDefaut)
{
    public const int MaxParAppelantParDefaut = 512;

    public const string CodeErreur = "JEA_JOURNAL";
    /// <summary>Aucune entrée de cet appelant ne porte cette op : déjà rendue par un rejeu, ou journal réécrit. Définitif, à la
    /// différence d'un journal illisible ou occupé : l'App ne reprend pas cet acquit.</summary>
    public const string CodeSansEntree = "JEA_ACQUIT_SANS_ENTREE";
    private readonly TimeSpan _attente = attente ?? TimeSpan.FromSeconds(10);

    private readonly FichierJsonAtomique _fichier = new(chemin, CodeErreur);
    private readonly string _chemin = chemin;

    private sealed record Ligne(Guid Id, string Op, string? ImageAvant, string Sid, int Pid, DateTimeOffset CreationDuProcessus,
        DateTimeOffset Le);

    public Result<EntreeDuJournal> Ajouter(PrivilegedOp op, string? imageAvant, AppelantDuJournal appelant, DateTimeOffset le)
    {
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
                return Result<IReadOnlyList<EntreeDuJournal>>.Fail(CodeErreur, $"Pending write {l.Id} carries an unreadable op.");
            entrees.Add(new EntreeDuJournal(l.Id, op, l.ImageAvant, new AppelantDuJournal(l.Sid, l.Pid, l.CreationDuProcessus), l.Le));
        }
        return Result<IReadOnlyList<EntreeDuJournal>>.Ok(entrees);
    }

    private Result<List<Ligne>> Lire()
    {
        // %ProgramData% laisse tout compte créer un dossier, et la réparation de la racine ne descend pas : un journal qui
        // n'est pas de confiance (propriétaire Administrateurs/SYSTEM, aucune écriture pour un autre) n'est pas de DrDXT.
        // Mis de côté, il n'est ni rejoué ni recopié ; s'il ne peut pas l'être, il est illisible et rien n'est écrit.
        if (File.Exists(_chemin) && !DrDxt.Reconciler.Manifest.TrustedManifestRoot.EnsureTrustedFile(_chemin).IsOk)
            return _fichier.MettreDeCote() ? Result<List<Ligne>>.Ok([]) : Result<List<Ligne>>.Fail(CodeErreur, "Untrusted journal.");
        var texte = _fichier.Read();
        if (!texte.IsOk) return Result<List<Ligne>>.Fail(texte.Error!.Value.Code, texte.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(texte.Value)) return Result<List<Ligne>>.Ok([]);
        try
        {
            return JsonSerializer.Deserialize<List<Ligne>>(texte.Value) is { } lignes && lignes.TrueForAll(l => l is not null)
                ? Result<List<Ligne>>.Ok(lignes)
                : Result<List<Ligne>>.Fail(CodeErreur, "Unreadable journal.");
        }
        catch (JsonException ex)
        {
            return Result<List<Ligne>>.Fail(CodeErreur, $"Unreadable journal: {ex.Message}");
        }
    }

    private Result SousVerrou(Func<List<Ligne>, Result> modifier)
    {
        try
        {
            // Le dossier est créé déjà fermé. Un dossier qui existait n'est jamais cru : %ProgramData% laisse tout compte en
            // créer un. Un lien est refusé avant d'y ouvrir quoi que ce soit (l'accès de sa cible serait réécrit), et encore
            // une fois le verrou ouvert dedans, quand il ne peut plus être remplacé. L'accès et le propriétaire
            // (Administrateurs : un propriétaire garde le droit de rendre l'accès) sont réparés sous le verrou.
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
            // Un fichier de confiance : propriétaire Administrateurs et accès protégé posés sur le temporaire avant le
            // renommage. Écrit sous le propriétaire par défaut (le compte qui a élevé), il en laissait réécrire l'accès par
            // un programme du même compte sans élévation, puis y forger des écritures que SYSTEM rejouerait.
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

    // Créé sous le propriétaire par défaut (le compte qui a élevé), le verrou laissait ce compte, sans élévation, s'en rendre
    // l'accès et le tenir ouvert : toute écriture journalisée était refusée. Il naît aux Administrateurs, sous SYSTEM et
    // Administrateurs seulement ; un verrou d'avant, au propriétaire non fiable, est remplacé une fois, sinon « occupé ».
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
