using System.Collections.Concurrent;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// LeaseWatchdog : l'« homme mort » (dead-man's switch) côté helper pour les leviers privilégiés tenus.
//
// Raison : un décalage d'horloge GPU (ou une limite de puissance) tenu, laissé appliqué après la
// mort du processus qui le pilote, est un vrai danger. Le chien de garde garantit qu'un levier
// privilégié tenu ne reste jamais appliqué si son contrôleur cesse de se signaler :
//   * Establish enregistre l'op de retour du levier + une échéance (maintenant + leaseSeconds).
//   * Renew (battement de cœur) repousse l'échéance tant que le contrôleur vit.
//   * TickAsync (mené par une surveillance de fond) lance le retour enregistré de tout bail
//     expiré, puis l'efface.
//
// Sécurité (le helper est la surface de risque n°1 : chaque invariant est gardé) :
//   * Le retour est le JSON du codec d'une feuille PrivilegedOp typée existante. Il est relié à
//     nouveau par le même PrivilegedOpCodec borné (pas de Type.GetType, pas de réflexion) et
//     revalidé à la feuille près par OpAllowlist à l'enregistrement (un retour hostile ou hors
//     allowlist est refusé d'emblée) et encore à l'échéance (défense en profondeur). Il passe
//     ensuite par l'IPrivilegedOpExecutor existant : aucune nouvelle primitive privilégiée générique.
//   * Les baux sont liés à l'identité authentifiée de l'appelant (LeaseOwner, tirée du process id
//     résolu par le pipe). Seul le propriétaire qui a établi un bail peut le renouveler ou le
//     clore ; tout autre appelant est refusé (ELEVATION_DENIED). Un propriétaire non identifié est
//     refusé (refus par défaut).
//   * L'état vit en mémoire seulement (aucune persistance disque : ce serait une surface
//     d'attaque). Si le helper meurt lui-même, les décalages NVAPI sont volatils (un redémarrage
//     les efface) : c'est le filet documenté, pas un cas contourné.
//   * Rien ne lève à travers la frontière : un échec de retour à l'échéance est journalisé via
//     IDiagnosticsLog, remonte un code d'erreur et est repris au mieux au tick suivant (le bail
//     est gardé jusqu'à ce qu'un retour réussisse).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class LeaseWatchdog
{
    private readonly IClock _clock;
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;
    private readonly IDiagnosticsLog _log;

    // Clé (owner, leaseId). ConcurrentDictionary pour que le tick de fond et un
    // Establish/Renew/Complete du thread IPC ne corrompent jamais le registre.
    private readonly ConcurrentDictionary<LeaseKey, LeaseEntry> _leases = new();
    // Les baux que ce helper-là a rendus lui-même : seul cas où l'App peut lever son journal. Un helper relancé n'en connaît
    // aucun, et son « inconnu » ne veut pas dire « rendu ». Jamais vidé : quelques ids par session de l'App.
    private readonly ConcurrentDictionary<LeaseKey, byte> _rendus = new();

    // Le nom d'un bail dont l'inverse est en train de partir. Un nouveau bail du même nom
    // (même réglage, quel que soit le client) est refusé pendant ce temps : sinon son écriture pourrait partir avant
    // l'inverse de l'ancien, qui la déferait (l'écran dirait « gardé », la carte serait rendue).
    private readonly ConcurrentDictionary<string, int> _inversesEnCours = new(StringComparer.Ordinal);

    public LeaseWatchdog(
        IClock clock,
        OpAllowlist allowlist,
        IPrivilegedOpExecutor executor,
        IDiagnosticsLog log)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Enregistre le retour automatique d'un levier tenu. Le retour est relié et
    /// validé à la feuille près dès maintenant (un retour hostile ou hors allowlist est refusé
    /// d'emblée). Lie le bail au <paramref name="owner"/> authentifié.</summary>
    /// <summary>Le nombre de baux tenus (le helper ne s'arrête pas pour inactivité tant qu'il y en a).</summary>
    public int NombreDeBaux => _leases.Count;

    /// <summary>Un bail ne dure pas plus de 10 minutes par renouvellement, et un appelant n'en
    /// tient pas plus de 8 (sinon un appelant gardait le helper administrateur vivant pour toujours).</summary>
    public const int DureeMaxSecondes = 600;
    public const int BauxMaxParAppelant = 8;

    public Result Establish(LeaseOwner owner, string leaseId, string revertOpJson, int leaseSeconds)
    {
        if (!owner.IsIdentified)
            return Deny("Lease owner is not an authenticated caller.");
        if (string.IsNullOrWhiteSpace(leaseId))
            return Deny("Lease id is required.");
        if (leaseSeconds <= 0)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease duration must be positive.");
        if (leaseSeconds > DureeMaxSecondes)
            return Result.Fail(ErrorCodes.NotAllowlisted, $"Lease duration above {DureeMaxSecondes} s.");
        if (_leases.Keys.Count(k => k.Owner == owner && k.LeaseId != leaseId) >= BauxMaxParAppelant)
            return Deny($"More than {BauxMaxParAppelant} leases for this caller.");
        if (_inversesEnCours.ContainsKey(leaseId))
            return Result.Fail(ErrorCodes.HelperOpFailed, Docteur.T("LW.RetourEnCours"));
        // Un autre client (une App plantée puis relancée) tient encore ce réglage ; son
        // bail expirera et rendra l'origine. Accepter le nouveau laissait cet inverse défaire la pose qu'on affichait « gardée ».
        if (_leases.Keys.Any(k => k.LeaseId == leaseId && k.Owner != owner))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                Docteur.T("LW.AutreDrDxt"));

        // Relier par le même codec borné (pas de résolution par nom de type)...
        var decoded = PrivilegedOpCodec.Deserialize(revertOpJson);
        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease revert op could not be bound.");

        // ... et valider à la feuille près, exactement comme toute op privilégiée.
        var allowed = _allowlist.Validate(decoded.Value);
        if (!allowed.IsOk)
            return allowed;

        // Un op firmware ne peut jamais être le revert d'un bail, même s'il passe
        // l'allowlist. Le déclencheur normal d'un dead-man's switch est la mort du contrôleur :
        // le revert part donc précisément quand plus personne n'est devant la machine. Un
        // redémarrage vers le setup firmware y arriverait chez le client comme une machine qui
        // repart dans le BIOS toute seule, sans rien à l'écran pour l'expliquer.
        //
        // C'est le même invariant que l'omission de la couture côté ReconcilerHost, et sans ce
        // garde il serait percé par ici : l'allowlist autorise l'op (à juste titre, pour un clic
        // utilisateur), et le registre de baux l'exécuterait ensuite sans surveillance.
        if (decoded.Value is RebootToFirmwareSetupOp or CancelPendingFirmwareBootOp)
            return Deny("A firmware-boot op can never be a lease revert: it would fire with nobody at the machine.");
        // Même invariant : une écriture BCD partie sans personne devant la machine ne se
        // verrait qu'au démarrage suivant. Les ops BCD restent des gestes du joueur, jamais d'un bail.
        if (decoded.Value is RestartDeviceOp or RemoveDriverPackageOp or RestoreDriverPackageOp)
            return Deny("A device or driver-store op can never be a lease revert: it would act with nobody at the machine.");
        if (decoded.Value is SetBcdValueOp or DeleteBcdValueOp or ScheduleMemoryDiagnosticOp or CancelMemoryDiagnosticOp)
            return Deny("A boot-configuration op can never be a lease revert: it would change the next boot unattended.");
        // Un ordre de bail n'est pas executable par l'executeur (le serveur le route vers ce
        // registre). En inverse, il echouait a chaque passage, remis et rejoue toutes les 5 s, sans fin.
        if (decoded.Value is EstablishLeaseOp or RenewLeaseOp)
            return Deny("A lease op can never be a lease revert: the executor cannot run it.");

        var key = new LeaseKey(owner, leaseId);
        var expiry = _clock.UtcNow.AddSeconds(leaseSeconds);
        var entry = new LeaseEntry(decoded.Value, expiry);

        // AddOrUpdate : rétablir le même (owner, id) le rafraîchit.
        _leases[key] = entry;
        _rendus.TryRemove(key, out _);
        return Result.Ok();
    }

    /// <summary>Battement de cœur : repousse l'échéance d'un bail existant à maintenant + leaseSeconds.
    /// Seul le propriétaire peut renouveler ; un bail inconnu (ou un autre appelant) est refusé :
    /// un renouvellement ne crée jamais de bail.</summary>
    public Result Renew(LeaseOwner owner, string leaseId, int leaseSeconds)
    {
        if (!owner.IsIdentified)
            return Deny("Lease owner is not an authenticated caller.");
        if (string.IsNullOrWhiteSpace(leaseId))
            return Deny("Lease id is required.");
        if (leaseSeconds <= 0)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease duration must be positive.");

        var key = new LeaseKey(owner, leaseId);
        // La clé inclut le propriétaire : un autre appelant ne trouve donc aucun bail →
        // refusé (il ne peut jamais atteindre, encore moins prolonger, le bail d'un autre).
        if (!_leases.TryGetValue(key, out var existing))
            return _rendus.ContainsKey(key)
                ? Result.Fail(ErrorCodes.LeaseReverted, "This helper reverted the lease: its original state is back.")
                : _inversesEnCours.ContainsKey(leaseId) ? EnCoursDeRendu()
                : Result.Fail(ErrorCodes.LeaseUnknown, "No such lease for this caller.");
        if (leaseSeconds > DureeMaxSecondes)
            return Result.Fail(ErrorCodes.NotAllowlisted, $"Lease duration above {DureeMaxSecondes} s.");

        var renewed = existing with { Expiry = _clock.UtcNow.AddSeconds(leaseSeconds) };
        // Remplacer seulement si c'est encore l'entrée observée (pas de mise à jour perdue). Prise entre-temps par le chien de
        // garde (il a pris le bail pour lancer son inverse, par exemple au réveil de veille) : le bail n'existe plus, répondre Ok serait faux.
        return _leases.TryUpdate(key, renewed, existing) ? Result.Ok() : EnCoursDeRendu();
    }

    // Passager : le battement continue, et le suivant lit l'issue (rendu, ou bail remis après un inverse raté).
    private static Result EnCoursDeRendu()
        => Result.Fail(ErrorCodes.LeaseReverting, "The lease expired while renewing; its inverse is running.");

    /// <summary>Le contrôleur a fait lui-même un retour propre : abandonner le bail pour que le
    /// chien de garde ne fasse jamais de retour automatique. Seul le propriétaire peut clore son bail.</summary>
    public Result Complete(LeaseOwner owner, string leaseId)
    {
        if (!owner.IsIdentified)
            return Deny("Lease owner is not an authenticated caller.");
        if (string.IsNullOrWhiteSpace(leaseId))
            return Deny("Lease id is required.");

        var key = new LeaseKey(owner, leaseId);
        if (!_leases.TryRemove(key, out _))
            return Deny("No such lease for this caller.");
        return Result.Ok();
    }

    /// <summary>Un passage de surveillance : lance le retour enregistré de chaque bail expiré,
    /// puis l'efface. Si le retour échoue, le bail est gardé (reprise au mieux au tick suivant)
    /// et l'échec est journalisé. Ne lève jamais.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        foreach (var kvp in _leases)
        {
            ct.ThrowIfCancellationRequested();
            if (kvp.Value.Expiry > now)
                continue; // pas encore expiré

            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>L'arret du helper : rend tous les baux encore actifs, expires ou non. Le registre vit
    /// en memoire ; une fois le helper parti, personne ne peut plus les renouveler ni les rendre, et un plafond
    /// d'envoi pose pour une partie restait jusqu'au redemarrage. Meme chemin que l'expiration (revalidation
    /// feuille-exacte, jamais d'exception).</summary>
    public async Task RendreToutAsync(CancellationToken ct)
    {
        foreach (var kvp in _leases)
            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
    }

    // L'inverse reste « en cours » jusqu'à ce que son issue soit écrite (rendu, ou bail remis) : un battement qui arrive
    // entre les deux n'apprend jamais un « bail inconnu » qui couperait le battement pour de bon. Compté : l'arrêt du helper
    // et un passage de surveillance peuvent viser le même bail, celui qui sort tôt n'efface pas la marque de l'autre.
    private async Task FireRevertAsync(LeaseKey key, LeaseEntry entry, CancellationToken ct)
    {
        _inversesEnCours.AddOrUpdate(key.LeaseId, 1, (_, n) => n + 1);
        try { await RejouerInverseAsync(key, entry, ct).ConfigureAwait(false); }
        finally
        {
            if (_inversesEnCours.AddOrUpdate(key.LeaseId, 0, (_, n) => n - 1) <= 0)
                _inversesEnCours.TryRemove(new KeyValuePair<string, int>(key.LeaseId, 0));
        }
    }

    private async Task RejouerInverseAsync(LeaseKey key, LeaseEntry entry, CancellationToken ct)
    {
        Result result;
        try
        {
            // Défense en profondeur : revalider l'op enregistrée à la feuille près avant de
            // l'exécuter. L'allowlist est l'autorisation, pas une barrière franchie une fois pour toutes.
            var allowed = _allowlist.Validate(entry.RevertOp);
            if (!allowed.IsOk)
            {
                // Une op enregistrée qui n'est plus dans l'allowlist : l'abandonner (jamais
                // d'exécution d'une op hors allowlist) et journaliser l'anomalie.
                _leases.TryRemove(key, out _);
                _log.Change(LeaseTweakId(key), "expired", "revert-rejected-not-allowlisted", "DrDXT");
                return;
            }

            // Le bail quitte le registre avant que son inverse tourne, et seulement s'il est encore
            // l'entrée expirée observée. Un Renew qui arrive pendant l'inverse est alors refusé (le contrôleur
            // rétablit depuis un état connu) au lieu de répondre Ok pour un bail sur le point d'être abandonné.
            // S'il a été renouvelé ou clos depuis le balayage, ce n'est plus à nous de le défaire.
            if (!_leases.TryRemove(new KeyValuePair<LeaseKey, LeaseEntry>(key, entry)))
                return;

            result = await _executor.ExecuteAsync(entry.RevertOp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _leases.TryAdd(key, entry); // pas un échec de retour : le tick suivant réessaie
            throw; // une annulation n'est pas un échec de retour : propager
        }
        catch (Exception ex)
        {
            // Un exécuteur qui lève ne doit jamais traverser cette frontière. Remettre le bail
            // pour que le tick suivant réessaie ; journaliser l'échec.
            var echec = $"revert-threw:{ex.GetType().Name}";
            _leases.TryAdd(key, entry with { DernierEchec = echec });
            if (echec != entry.DernierEchec) _log.Change(LeaseTweakId(key), "expired", echec, "DrDXT");
            return;
        }

        if (result.IsOk)
        {
            // Retour fait : déjà hors du registre, il ne se redéclenche jamais.
            _rendus[key] = 0;
            _log.Change(LeaseTweakId(key), "expired", "auto-reverted", "DrDXT");
        }
        else
        {
            // Remettre le bail (reprise au mieux au tick suivant) + journaliser l'erreur.
            // TryAdd : si le contrôleur a rétabli entre-temps, son nouveau bail l'emporte.
            // Rejoue a chaque passage (abandonner laisserait l'etat en place), mais journalise
            // seulement quand l'erreur change : le journal du helper n'a pas de borne.
            var echec = $"revert-failed:{result.Error?.Code}";
            _leases.TryAdd(key, entry with { DernierEchec = echec });
            if (echec != entry.DernierEchec) _log.Change(LeaseTweakId(key), "expired", echec, "DrDXT");
        }
    }

    private static string LeaseTweakId(LeaseKey key) => $"lease:{key.LeaseId}";

    private static Result Deny(string message) => Result.Fail(ErrorCodes.ElevationDenied, message);

    // Identité (owner, leaseId) du registre. Limitée au propriétaire : un appelant ne peut
    // jamais viser que ses propres baux.
    private readonly record struct LeaseKey(LeaseOwner Owner, string LeaseId);

    // Le retour enregistré + le moment où il doit partir sans renouvellement. Immuable ; Renew
    // fait une copie avec une nouvelle Expiry.
    private sealed record LeaseEntry(PrivilegedOp RevertOp, DateTimeOffset Expiry, string? DernierEchec = null);
}

// -----------------------------------------------------------------------
// LeaseOwner : l'identité authentifiée propriétaire d'un bail.
//
// Tirée de CallerInfo.ClientProcessId (le pid du pair résolu par le pipe, dont
// l'authentificateur d'appelant a déjà vérifié qu'il est le DrDxt.App de confiance). Lier le
// bail à cette identité fait toute l'auth : seul le même appelant authentifié peut renouveler
// ou clore le bail qu'il a établi. Un pid non résolu → non identifié → refusé.
// -----------------------------------------------------------------------
public readonly record struct LeaseOwner(int? ProcessId = null)
{
    public bool IsIdentified => ProcessId is { } pid && pid > 0;
}
