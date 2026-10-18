using System.Diagnostics;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>
/// L'exécuteur derrière le journal d'écriture anticipée. Seule une pose marquée (PoseEnAttenteDOrigineOp) dont l'op est
/// journalisée (ClassesDuJournal) passe au journal : la valeur d'avant de sa cible est lue et notée sur le disque avec son
/// demandeur, puis l'op part, nue ; une cible illisible, un demandeur inconnu ou un journal qui refuse, et rien n'est
/// exécuté. Une écriture qui échoue sort du journal. Toute op nue passe telle quelle : une annulation, un retour de bail, une
/// réaffirmation du service ne sont jamais journalisés, donc jamais rejoués. AcquitterEcritureOp retire l'entrée de son
/// seul appelant.
/// </summary>
public sealed class ExecuteurJournalise(
    IPrivilegedOpExecutor interne,
    JournalDEcritureAnticipee journal,
    Func<PrivilegedOp, Func<Result<string?>>?> lecteur,
    Func<CallerInfo, AppelantDuJournal?> identifier,
    Func<DateTimeOffset> horloge) : IPrivilegedOpExecutor
{
    public const string CodeRefus = "JEA_REFUS";
    /// <summary>Un acquit refusé : il arrive après l'écriture, la pose est en place. Jamais sous CodeRefus ni sous le code du
    /// journal, qui disent « rien n'a été écrit ».</summary>
    public const string CodeAcquitRefuse = "JEA_ACQUIT_REFUSE";

    /// <summary>Sans demandeur, une pose marquée ou un acquit ne peuvent être notés : refusés. Une op nue passe.</summary>
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => op switch
    {
        PoseEnAttenteDOrigineOp => Task.FromResult(Result.Fail(CodeRefus, "Unknown caller: the write-ahead journal cannot record it.")),
        AcquitterEcritureOp => Task.FromResult(Result.Fail(CodeAcquitRefuse, "Unknown caller: the acknowledgement cannot be matched.")),
        _ => interne.ExecuteAsync(op, ct),
    };

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct, CallerInfo appelant)
    {
        switch (op)
        {
            case AcquitterEcritureOp ae:
                if (Interieure(ae.PoseeJson) is not { } posee) return Result.Fail(CodeAcquitRefuse, "The acknowledged op cannot be read.");
                // Le miroir de l'enveloppe : une pose qui n'a pas été journalisée n'a rien à acquitter.
                if (ClassesDuJournal.De(posee) != ClasseDuJournal.Journalisee) return Result.Ok();
                if (Identifier(appelant) is not { } qui)
                    return Result.Fail(CodeAcquitRefuse, "The caller process cannot be identified.");
                // « Sans entrée » reste tel quel : définitif, l'App ne reprend pas cet acquit. Tout autre refus (journal occupé,
                // illisible) se reprend.
                var acquit = journal.Acquitter(posee, qui);
                return acquit.IsOk || acquit.Error?.Code == JournalDEcritureAnticipee.CodeSansEntree
                    ? acquit
                    : Result.Fail(CodeAcquitRefuse, acquit.Error?.Message ?? "The acknowledgement was refused.");
            case PoseEnAttenteDOrigineOp pe:
                if (Interieure(pe.PoseJson) is not { } pose) return Result.Fail(CodeRefus, "The carried op cannot be read.");
                return ClassesDuJournal.De(pose) == ClasseDuJournal.Journalisee
                    ? await JournaliserPuisExecuter(pose, appelant, ct).ConfigureAwait(false)
                    : await interne.ExecuteAsync(pose, ct).ConfigureAwait(false);
            default:
                return await interne.ExecuteAsync(op, ct).ConfigureAwait(false);
        }
    }

    private async Task<Result> JournaliserPuisExecuter(PrivilegedOp op, CallerInfo appelant, CancellationToken ct)
    {
        AppelantDuJournal? demandeur;
        Result<string?> avant;
        try
        {
            demandeur = identifier(appelant);
            if (demandeur is null)
                return Result.Fail(CodeRefus, "The caller process cannot be identified.");
            if (lecteur(op) is not { } lire)
                return Result.Fail(CodeRefus, $"No read of the target of {op.GetType().Name}.");
            // Les lectures (registre, NVAPI, ADLX) peuvent lever : rien ne s'exécute sans une image notée.
            avant = lire();
        }
        catch (Exception ex)
        {
            return Result.Fail(CodeRefus, $"The target or the caller cannot be read before writing: {ex.Message}");
        }
        if (!avant.IsOk)
            return Result.Fail(CodeRefus, $"The target cannot be read before writing: {avant.Error?.Message}");
        var entree = journal.Ajouter(op, avant.Value, demandeur, horloge());
        if (!entree.IsOk)
            return Result.Fail(entree.Error!.Value.Code, entree.Error.Value.Message);

        var r = await interne.ExecuteAsync(op, ct).ConfigureAwait(false);
        // Un échec qui laisse la cible changée garde son entrée : c'est le cas où le rejeu doit rendre l'origine.
        if (!r.IsOk && r.Error?.Code != ErrorCodes.TweakPartiallyApplied)
            journal.Retirer(entree.Value!.Id);
        return r;
    }

    private AppelantDuJournal? Identifier(CallerInfo appelant)
    {
        try { return identifier(appelant); }
        catch (Exception) { return null; }
    }

    // L'allowlist a déjà refusé une enveloppe dans une enveloppe ; elle est refusée ici aussi.
    private static PrivilegedOp? Interieure(string json)
        => PrivilegedOpCodec.Deserialize(json) is { IsOk: true, Value: { } op } && op is not (PoseEnAttenteDOrigineOp or AcquitterEcritureOp)
            ? op
            : null;

    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interne.ReadAsync(op, ct);
}

/// <summary>Le demandeur d'une écriture, tiré de son PID : son compte et l'heure de création de son processus, en UTC.
/// Lecture seule. Un PID absent, sans processus ou illisible ne donne rien.</summary>
public static class IdentiteDesAppelants
{
    public static AppelantDuJournal? De(CallerInfo appelant)
    {
        if (appelant.ClientProcessId is not { } pid) return null;
        try
        {
            using var processus = Process.GetProcessById(pid);
            var creation = new DateTimeOffset(processus.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (!OpenProcessToken(processus.Handle, TokenQuery, out var jeton)) return null;
            try
            {
                using var identite = new WindowsIdentity(jeton);
                return identite.User is { } sid ? new AppelantDuJournal(sid.Value, pid, creation) : null;
            }
            finally { Natif.CloseHandle(jeton); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private const uint TokenQuery = 0x0008;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
}
