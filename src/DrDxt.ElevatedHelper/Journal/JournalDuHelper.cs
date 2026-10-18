using DrDxt.Core;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>
/// Le journal d'écriture anticipée tel que chaque hôte (helper, service, désinstallation) le construit. L'ordre est
/// obligatoire : la racine (%ProgramData%\DrDxt) est réparée d'abord, le dossier du journal naît ensuite dedans, à la
/// première écriture. Une racine que la réparation refuse ne donne pas de journal : l'exécuteur reste l'intérieur, qui refuse
/// une pose marquée (WRITE_AHEAD_JOURNAL_UNAVAILABLE) plutôt que de l'écrire sans filet.
/// Le chien de garde des baux tient l'exécuteur intérieur : le retour d'un bail est une annulation, jamais journalisée.
/// </summary>
public static class JournalDuHelper
{
    public const string Dossier = "jea";

    public static JournalDEcritureAnticipee? Ouvrir(string racine, Func<string, Result> reparerLaRacine)
        => reparerLaRacine(racine).IsOk ? new JournalDEcritureAnticipee(Path.Combine(racine, Dossier, "journal.json")) : null;

    public static IPrivilegedOpExecutor Decorer(IPrivilegedOpExecutor interieur, JournalDEcritureAnticipee? journal,
        Func<PrivilegedOp, Func<Result<string?>>?> lecteur)
        => journal is null ? interieur : new ExecuteurJournalise(interieur, journal, lecteur, IdentiteDesAppelants.De, () => DateTimeOffset.UtcNow);

    /// <summary>Hors du helper : la lecture qui relit chaque cible avant d'écrire.</summary>
    public static IPrivilegedOpExecutor Decorer(IPrivilegedOpExecutor interieur, JournalDEcritureAnticipee? journal,
        DrDxt.ElevatedHelper.Execution.LectureDesCiblesWin32 lecture)
        => Decorer(interieur, journal, lecture.Lecteur);

    /// <summary>Le rejeu, une fois au démarrage de l'hôte : ce qu'un demandeur mort a laissé est rendu, par le client en
    /// processus de l'hôte (sa liste blanche). Null sans journal.</summary>
    public static async Task<RapportDuRendu?> RejouerAsync(JournalDEcritureAnticipee? journal, ILectureDesCibles lecture,
        IElevatedClient ecriture, Func<AppelantDuJournal, bool> vivant, Func<string?> sidCourant, CancellationToken ct)
    {
        if (journal is null) return null;
        var r = await new RejeuDuJournal(journal, lecture, ecriture, vivant, sidCourant).ExecuterAsync(ct).ConfigureAwait(false);
        return r.IsOk ? r.Value : new RapportDuRendu($"{r.Error?.Code} : {r.Error?.Message}", Array.Empty<LigneDuRendu>());
    }
}
