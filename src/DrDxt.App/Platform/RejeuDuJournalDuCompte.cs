using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.App.Platform;

/// <summary>
/// Au lancement de l'App : ce qu'une App morte a posé dans la session (HKCU) sans en noter l'origine est rendu, avec les règles
/// du rejeu du helper (RejeuDuJournal : rendu si la valeur posée est encore là, respecté sinon). Par cette App, avec son jeton :
/// la ruche est la sienne. Rien ne part au helper : le journal du compte, que le compte peut réécrire, ne fait écrire que des
/// valeurs de sa propre session.
/// </summary>
public static class RejeuDuJournalDuCompte
{
    /// <summary>Le texte de ce qui a été jugé, ou null. Ne lève jamais.</summary>
    public static async Task<string?> TexteAsync(JournalDEcritureAnticipee journal, ILectureDesCibles lecture,
        IRegistryActions? registre = null, Func<AppelantDuJournal, bool>? vivant = null)
        => (await RejouerAsync(journal, lecture, registre, vivant).ConfigureAwait(false)).Texte;

    /// <summary>Le texte de ce qui a été jugé (ou null), et les lignes jugées, pour reconnaître un levier rendu. Ne lève jamais.</summary>
    public static async Task<(string? Texte, IReadOnlyList<RapportDuRejeu.Ligne> Lignes)> RejouerAsync(JournalDEcritureAnticipee journal,
        ILectureDesCibles lecture, IRegistryActions? registre = null, Func<AppelantDuJournal, bool>? vivant = null)
    {
        try
        {
            var rendu = await JournalDuHelper.RejouerAsync(journal, lecture, new ClientSessionUtilisateur(new RienAuHelper(), registre),
                vivant ?? RejeuDuJournal.ProcessusVivant, RejeuDuJournal.CompteCourant, CancellationToken.None).ConfigureAwait(false);
            if (rendu is null) return (null, []);
            var jugees = RapportDuRejeu.De(rendu, DateTimeOffset.UtcNow).Lignes.Where(l => !l.EnSuspens).ToList();
            return (jugees.Count == 0 ? null : RejeuDuJournalLu.Texte(jugees), jugees);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Session journal replay failed: {0}", ex);
            return (null, []);
        }
    }

    private sealed class RienAuHelper : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result.Fail(ErrorCodes.NotAllowlisted, "The session journal only writes the session's own values."));

        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result<byte[]>.Fail(ErrorCodes.NotAllowlisted, "The session journal reads nothing from the helper."));
    }
}
