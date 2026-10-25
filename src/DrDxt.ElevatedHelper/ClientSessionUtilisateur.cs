using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Journal;

namespace DrDxt.ElevatedHelper;

/// <summary>
/// Une valeur de la session de l'utilisateur (HKCU) s'écrit avec le jeton de l'App, jamais par le helper
/// administrateur. Élevé avec le compte d'un autre (UAC « par-dessus l'épaule »), le helper écrirait la ruche de cet autre
/// compte. Le code d'écriture reste celui du helper (sa liste blanche, ses actions de registre), exécuté dans le processus
/// de l'App pour la seule session de l'utilisateur ; tout le reste passe au helper.
/// Dans une portée de pose, une valeur journalisée suit la règle du journal d'écriture anticipée, dans celui du compte : sa
/// valeur d'avant est lue et notée sur le disque avant l'écriture, et la fermeture de la portée l'acquitte. L'App morte avant
/// d'avoir noté l'origine, l'App suivante la rend à son lancement (RejeuDuJournal). Une cible illisible ou un journal qui
/// refuse, et rien n'est écrit.
/// </summary>
/// <param name="journalDuCompte">Le journal du compte ; absent, les valeurs de la session partent sans filet.</param>
/// <param name="lecteur">La lecture d'une cible avant d'écrire ; par défaut celle du helper (LectureDesCiblesWin32).</param>
/// <param name="moi">Ce processus, tel que le journal le note ; par défaut lu de son PID.</param>
public sealed class ClientSessionUtilisateur(IElevatedClient eleve, IRegistryActions? registre = null,
    JournalDEcritureAnticipee? journalDuCompte = null, Func<PrivilegedOp, Func<Result<string?>>?>? lecteur = null,
    Func<AppelantDuJournal?>? moi = null) : IElevatedClient
{
    private static readonly OpAllowlist Liste = new();

    /// <summary>Le client qui porte tout le reste au helper.</summary>
    public IElevatedClient VersLeHelper => eleve;

    /// <summary>Sans relance du helper : le battement d'un bail ne porte que des ordres de bail, tous pour le helper.</summary>
    public IElevatedClient SansRelance => eleve.SansRelance;
    private readonly IRegistryActions _registre = registre ?? new Win32RegistryActions(Win32RegistryActions.TraceDeLaSession);
    private readonly Func<PrivilegedOp, Func<Result<string?>>?> _lecteur = lecteur ?? new LectureDesCiblesWin32().Lecteur;
    private readonly Func<AppelantDuJournal?> _moi = moi ?? (() => IdentiteDesAppelants.De(new Ipc.CallerInfo(Environment.ProcessId)));

    /// <summary>Les acquits du journal du compte refusés (journal occupé), un par pose, pour tout le processus : repris avant
    /// chaque envoi et à la fermeture, comme ceux du helper. Restés refusés, l'entrée reste, et le lancement suivant rendrait
    /// une origine pourtant notée.</summary>
    private static readonly List<(JournalDEcritureAnticipee Journal, SetRegistryValueOp Op, AppelantDuJournal Qui)> AcquitsRefuses = [];

    /// <summary>Un acquit du journal du compte attend encore, dans ce processus.</summary>
    public static bool UnAcquitLocalAttend { get { lock (AcquitsRefuses) return AcquitsRefuses.Count > 0; } }

    /// <summary>Renvoie chaque acquit refusé ; vrai quand il n'en reste aucun. « Sans entrée » : déjà rendu, rien à reprendre.</summary>
    public static bool ReprendreLesAcquitsLocaux()
    {
        lock (AcquitsRefuses)
        {
            AcquitsRefuses.RemoveAll(a => a.Journal.Acquitter(a.Op, a.Qui) is { IsOk: true } or { Error.Code: JournalDEcritureAnticipee.CodeSansEntree });
            return AcquitsRefuses.Count == 0;
        }
    }

    private static Result Acquitter(JournalDEcritureAnticipee journal, SetRegistryValueOp op, AppelantDuJournal qui)
    {
        var r = journal.Acquitter(op, qui);
        if (r is { IsOk: false } and not { Error.Code: JournalDEcritureAnticipee.CodeSansEntree })
            lock (AcquitsRefuses) AcquitsRefuses.Add((journal, op, qui));
        return r;
    }

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        if (UnAcquitLocalAttend) ReprendreLesAcquitsLocaux();
        return Envoyer(op, ct);
    }

    private Task<Result> Envoyer(PrivilegedOp op, CancellationToken ct) => op switch
    {
        SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } s => Task.FromResult(
            Liste.Validate(s) is { IsOk: false } refus ? refus
            : PorteeDePose.Courante is { } portee && journalDuCompte is not null && ClassesDuJournal.De(s) == ClasseDuJournal.Journalisee
                ? EcrireJournalise(s, portee, journalDuCompte)
                : _registre.SetValue(s.Hive, s.Path, s.Name, s.Kind, s.Value)),
        DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } d => Task.FromResult(
            Liste.Validate(d) is { IsOk: false } refus ? refus : _registre.DeleteValue(d.Hive, d.Path, d.Name)),
        // Un programme au démarrage de la session : sa valeur et son dossier Démarrage sont ceux de l'utilisateur.
        SetStartupApprovalOp { Hive: RegistryHiveKind.CurrentUser } a => Task.FromResult(
            Liste.Validate(a) is { IsOk: false } refus ? refus : _registre.SetStartupApproval(a.Hive, a.Liste, a.Nom, a.Actif)),
        _ => eleve.ExecuteAsync(op, ct),
    };

    /// <summary>La règle de ExecuteurJournalise, dans le processus de l'App : rien ne s'écrit sans une image notée.</summary>
    private Result EcrireJournalise(SetRegistryValueOp s, PorteeDePose portee, JournalDEcritureAnticipee journal)
    {
        AppelantDuJournal? qui;
        Result<string?> avant;
        try
        {
            qui = _moi();
            if (qui is null) return Result.Fail(ExecuteurJournalise.CodeRefus, "This process cannot be identified for the journal.");
            if (_lecteur(s) is not { } lire) return Result.Fail(ExecuteurJournalise.CodeRefus, "No read of the target.");
            avant = lire();
        }
        catch (Exception ex)
        {
            return Result.Fail(ExecuteurJournalise.CodeRefus, $"The target cannot be read before writing: {ex.Message}");
        }
        if (!avant.IsOk) return Result.Fail(ExecuteurJournalise.CodeRefus, $"The target cannot be read before writing: {avant.Error?.Message}");
        var entree = journal.Ajouter(s, avant.Value, qui, DateTimeOffset.UtcNow);
        if (!entree.IsOk) return Result.Fail(entree.Error!.Value.Code, entree.Error.Value.Message);

        var r = _registre.SetValue(s.Hive, s.Path, s.Name, s.Kind, s.Value);
        if (r.IsOk) portee.Noter(() => Task.FromResult(Acquitter(journal, s, qui)));
        // Un échec qui laisse la cible changée garde son entrée : le rejeu rendra l'origine.
        else if (r.Error?.Code != ErrorCodes.TweakPartiallyApplied) journal.Retirer(entree.Value!.Id);
        return r;
    }

    // Les lectures (santé de Windows, variables firmware) vont au helper ; sans cette ligne,
    // la méthode par défaut de l'interface répondait « pas de canal de retour ».
    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => eleve.ReadAsync(op, ct);
}
