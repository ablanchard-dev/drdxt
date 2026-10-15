using DrDxt.Core;

namespace DrDxt.Reconciler.Reseau;

/// <summary>
/// La protection du ping portée par le service de démarrage, sans DrDXT ouvert et sans invite.
///
/// À chaque battement, le réglage retenu (<see cref="PlafondEnvoiDuService"/>, écrit par le helper) est relu :
/// le plafond est posé quand le jeu retenu tourne, retiré quand il se ferme ou quand le joueur éteint la
/// protection. Chaque ordre passe par le client en processus du service, donc par la même allowlist que le helper.
///
/// Pas de bail ici, à la différence du mode de l'App : le bail protège contre un helper qui survivrait à l'App
/// qui l'a lancé. Dans le service, celui qui pose est celui qui retire. Le filet est ailleurs : retrait au
/// démarrage (un plafond resté d'une vie précédente), retrait à l'arrêt, et les politiques QoS vivent en
/// mémoire (ActiveStore) : un redémarrage les efface.
/// </summary>
public sealed class ProtectionDuPingParLeService
{
    private readonly IElevatedClient _client;
    private readonly Func<PlafondRetenuDuService?> _lireRetenu;
    private readonly Func<string, bool> _jeuEnCours;
    private readonly SemaphoreSlim _uneALaFois = new(1, 1);
    private PlafondRetenuDuService? _pose;
    private readonly IDiagnosticsLog? _journal;

    public const string IdJournal = "upload-cap-service";

    /// <param name="journal">Chaque pose et chaque retrait reussis s'y ecrivent : sans lui, la protection portee par le
    /// service serait invisible apres coup.</param>
    public ProtectionDuPingParLeService(IElevatedClient client, Func<PlafondRetenuDuService?> lireRetenu, Func<string, bool> jeuEnCours,
        IDiagnosticsLog? journal = null)
        => (_client, _lireRetenu, _jeuEnCours, _journal) = (client, lireRetenu, jeuEnCours, journal);

    /// <summary>Ce qui est posé à notre connaissance, ou <c>null</c>.</summary>
    public PlafondRetenuDuService? Pose => _pose;

    /// <summary>Retire ce qui resterait d'une vie précédente du service. Le retrait est idempotent.</summary>
    public async Task DemarrerAsync(CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _client.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false);
            _pose = null;
        }
        finally { _uneALaFois.Release(); }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var retenu = _lireRetenu();
            var voulu = retenu is not null && _jeuEnCours(retenu.Exe) ? retenu : null;
            if (voulu == _pose) return;

            if (voulu is null)
            {
                if ((await _client.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false)).IsOk)
                {
                    _journal?.Change(IdJournal, "posé", "retiré", "DrDXT");
                    _pose = null;
                }
            }
            else if ((await _client.ExecuteAsync(new ApplyUploadCapOp(voulu.Exe, voulu.PlafondBps), ct).ConfigureAwait(false)).IsOk)
            {
                _journal?.Change(IdJournal, _pose is null ? "retiré" : "posé",
                    FormattableString.Invariant($"{voulu.PlafondBps / 1e6:0.#} Mbit/s for {voulu.Exe}"), "DrDXT");
                _pose = voulu;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lecture du réglage ou des processus en échec : l'état reste celui d'avant, le battement suivant réessaie.
        }
        finally { _uneALaFois.Release(); }
    }

    /// <summary>Le service s'arrête : rien ne doit rester posé derrière lui.</summary>
    public async Task ArreterAsync(CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((await _client.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false)).IsOk)
            {
                if (_pose is not null) _journal?.Change(IdJournal, "posé", "retiré", "DrDXT");
                _pose = null;
            }
        }
        finally { _uneALaFois.Release(); }
    }
}
