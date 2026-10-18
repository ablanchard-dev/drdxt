using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;

namespace DrDxt.App.Platform;

/// <summary>
/// Le client vers le helper, sous ClientSessionUtilisateur (une écriture HKCU reste faite dans la session). Dans une portée de
/// pose (PorteeDePose), une écriture journalisée part marquée (PoseEnAttenteDOrigineOp) et son acquit est noté pour la
/// fermeture de la portée ; tout le reste, et tout hors portée, part nu.
/// Un acquit refusé (helper parti, journal occupé) laisserait le helper rendre au prochain démarrage un réglage gardé : il
/// est repris avant chaque envoi suivant, battement compris, quel que soit le helper qui répond alors.
/// </summary>
public sealed class ClientDesPoses : IElevatedClient
{
    /// <summary>Les acquits refusés, un par pose. Le processus entier les partage : l'entrée du journal appartient à l'App,
    /// pas au client ni au helper qui l'a reçue.
    /// En mémoire seulement : un acquit ne retire que l'entrée de son processus (compte, PID, heure de création), et une App
    /// relancée en est un autre. Gardé sur disque, il ne retirerait rien, et un fichier du profil qui pourrait empêcher le
    /// rejeu ouvrirait une surface. L'App morte, son entrée est rendue au rejeu suivant : la machine revient à l'origine, le
    /// sens sûr, et le rapport du rejeu le dit.</summary>
    internal sealed class AcquitsRefuses
    {
        private readonly List<string> _json = [];
        public void Ajouter(string json) { lock (_json) _json.Add(json); }
        public void Retirer(string json) { lock (_json) _json.Remove(json); }
        public string[] Copie() { lock (_json) return [.. _json]; }
        /// <summary>Une reprise à la fois : deux reprises du même acquit en retireraient deux entrées au helper.</summary>
        public readonly SemaphoreSlim Reprise = new(1, 1);
    }

    private static readonly AcquitsRefuses DuProcessus = new();

    private readonly IElevatedClient _interne;
    private readonly AcquitsRefuses _refuses;

    public ClientDesPoses(IElevatedClient interne) : this(interne, DuProcessus) { }

    internal ClientDesPoses(IElevatedClient interne, AcquitsRefuses refuses)
    {
        _interne = interne;
        _refuses = refuses;
    }

    public IElevatedClient VersLeHelper => _interne;

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        await ReprendreAsync().ConfigureAwait(false);
        if (PorteeDePose.Courante is not { } portee || ClassesDuJournal.De(op) != ClasseDuJournal.Journalisee
            || PrivilegedOpCodec.Serialize(op) is not { IsOk: true, Value: { } json })
            return await _interne.ExecuteAsync(op, ct).ConfigureAwait(false);

        var r = await _interne.ExecuteAsync(new PoseEnAttenteDOrigineOp(json), ct).ConfigureAwait(false);
        // Une pose refusée est déjà sortie du journal du helper : rien à acquitter.
        if (r.IsOk) portee.Noter(() => AcquitterAsync(json));
        // Le helper s'est tu après l'avoir reçue : posée ou non, son entrée reste au journal, et le rejeu qui suit la mort de
        // cette App la rendra si elle est en place. L'écran peut le promettre ; pour une op nue, non.
        if (r.Error?.Code == ErrorCodes.HelperNoReply)
            return Result.Fail(ErrorCodes.HelperNoReplyJournalisee, r.Error.Value.Message);
        return r;
    }

    public async Task<Result> RenewLease(string leaseId, int leaseSeconds, CancellationToken ct)
    {
        await ReprendreAsync().ConfigureAwait(false);
        return await _interne.RenewLease(leaseId, leaseSeconds, ct).ConfigureAwait(false);
    }

    public IElevatedClient SansRelance => new ClientDesPoses(_interne.SansRelance, _refuses);

    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => _interne.ReadAsync(op, ct);

    /// <summary>À la fermeture normale de l'App : un dernier essai pour les acquits refusés. Le texte à montrer si l'un reste
    /// refusé, sinon null.</summary>
    public async Task<string?> DernierEssaiAsync()
        => await ReprendreAsync().ConfigureAwait(false) ? null : Docteur.T("JEA.AcquitRefuse");

    /// <summary>Un acquit refusé attend encore, dans ce processus.</summary>
    public static bool UnAcquitAttend => DuProcessus.Copie().Length > 0;

    /// <summary>Le texte à montrer à la fermeture de la fenêtre, ou null. Le dernier essai est borné : passé la borne, l'acquit
    /// est tenu pour refusé. Windows qui ferme la session n'attend pas une boîte : rien à montrer, l'essai seul.</summary>
    public static async Task<string?> TexteDeFermetureAsync(Func<Task<string?>> dernierEssai, Func<bool> sessionFinit, TimeSpan borne)
    {
        string? texte;
        try { texte = await dernierEssai().WaitAsync(borne).ConfigureAwait(false); }
        catch (Exception) { texte = Docteur.T("JEA.AcquitRefuse"); }
        return sessionFinit() ? null : texte;
    }

    private async Task<Result> AcquitterAsync(string json)
    {
        var r = await EnvoyerLAcquitAsync(json).ConfigureAwait(false);
        if (r is { IsOk: false } and not { Error.Code: JournalDEcritureAnticipee.CodeSansEntree }) _refuses.Ajouter(json);
        return r;
    }

    /// <summary>Renvoie chaque acquit refusé, le plus ancien d'abord. Vrai quand il n'en reste aucun.</summary>
    private async Task<bool> ReprendreAsync()
    {
        if (_refuses.Copie().Length == 0) return true;
        await _refuses.Reprise.WaitAsync().ConfigureAwait(false);
        try
        {
            var tous = true;
            foreach (var json in _refuses.Copie())
            {
                // Sans entrée, il n'y a plus rien à acquitter : le rejeu qui l'a rendue le dira dans son rapport.
                if (await EnvoyerLAcquitAsync(json).ConfigureAwait(false) is { IsOk: true } or { Error.Code: JournalDEcritureAnticipee.CodeSansEntree })
                    _refuses.Retirer(json);
                else tous = false;
            }
            return tous;
        }
        finally { _refuses.Reprise.Release(); }
    }

    private async Task<Result> EnvoyerLAcquitAsync(string json)
    {
        try { return await _interne.ExecuteAsync(new AcquitterEcritureOp(json), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { return Result.Fail(ErrorCodes.HelperUnreachable, ex.Message); }
    }
}
