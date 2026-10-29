using System.Collections.Concurrent;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// LeaseWatchdog : l'« homme mort » du helper. Un décalage d'horloge GPU ou une limite de puissance tenu ne reste jamais
// appliqué si son contrôleur cesse de se signaler : Establish enregistre l'op de retour et une échéance, Renew la
// repousse, TickAsync lance le retour de tout bail expiré puis l'efface.
//
// Sécurité (le helper est la surface de risque n°1) :
//   * Le retour est le JSON d'une feuille PrivilegedOp typée, relié par le PrivilegedOpCodec borné (ni Type.GetType ni
//     réflexion), revalidé à la feuille près par OpAllowlist à l'enregistrement et à l'échéance, puis exécuté par
//     l'IPrivilegedOpExecutor existant : aucune primitive privilégiée générique.
//   * Les baux sont liés à l'appelant authentifié (LeaseOwner, pid résolu par le pipe) : seul le propriétaire renouvelle
//     ou clôt ; tout autre est refusé (ELEVATION_DENIED), un propriétaire non identifié aussi.
//   * L'état vit en mémoire seulement (le disque serait une surface d'attaque). Le helper mort, ses baux et la garde
//     thermique meurent avec lui ; décalages NVAPI et vitesse fixe NVML sont volatils, l'App rend le ventilateur
//     (EssaiVentilateur) et son journal rend le reste au lancement suivant.
//   * Rien ne lève à travers la frontière : un retour raté est journalisé (IDiagnosticsLog) et retenté au tick suivant,
//     le bail gardé jusqu'au succès.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class LeaseWatchdog
{
    private readonly IClock _clock;
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;
    private readonly IDiagnosticsLog _log;

    // Clé (owner, leaseId) ; concurrent : le tick de fond et le thread IPC y touchent ensemble.
    private readonly ConcurrentDictionary<LeaseKey, LeaseEntry> _leases = new();
    // Les baux que ce helper-là a rendus lui-même : seul cas où l'App peut lever son journal. Un helper relancé n'en connaît
    // aucun, et son « inconnu » ne veut pas dire « rendu ». Jamais vidé : quelques ids par session de l'App.
    private readonly ConcurrentDictionary<LeaseKey, byte> _rendus = new();

    // Le nom d'un bail dont l'inverse part. Un nouveau bail du même nom (quel que soit le client) est refusé pendant ce
    // temps : l'inverse de l'ancien déferait son écriture (« gardé » à l'écran, carte rendue).
    private readonly ConcurrentDictionary<string, int> _inversesEnCours = new(StringComparer.Ordinal);

    // Le refus ci-dessus et l'inscription du nouveau bail ne font qu'un pas face à la marque d'un inverse : un inverse
    // marqué entre les deux déferait le réglage que l'App croit gardé.
    private readonly object _inscription = new();

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

    /// <summary>Le nombre de baux tenus (le helper ne s'arrête pas pour inactivité tant qu'il y en a).</summary>
    public int NombreDeBaux => _leases.Count;

    /// <summary>Un bail ne dure pas plus de 10 minutes par renouvellement, et un appelant n'en tient pas plus de 8 : sinon
    /// un appelant garderait le helper administrateur vivant pour toujours.</summary>
    public const int DureeMaxSecondes = 600;
    public const int BauxMaxParAppelant = 8;

    /// <summary>Enregistre le retour automatique d'un levier tenu, relié et validé à la feuille près dès maintenant (un
    /// retour hostile ou hors allowlist est refusé d'emblée). Lie le bail au <paramref name="owner"/> authentifié.</summary>
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
        // Un autre client (une App plantée puis relancée) tient encore ce réglage : son bail expirera, et son inverse
        // déferait la pose affichée « gardée ».
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

        // Un revert de bail part quand plus personne n'est devant la machine (la mort du contrôleur). Une op firmware,
        // autorisée par l'allowlist pour un clic, relancerait la machine dans le BIOS sans explication : jamais un revert
        // (même invariant que l'omission de la couture côté ReconcilerHost).
        if (decoded.Value is RebootToFirmwareSetupOp or CancelPendingFirmwareBootOp)
            return Deny("A firmware-boot op can never be a lease revert: it would fire with nobody at the machine.");
        // Même invariant pour les gestes sur un périphérique ou le magasin de pilotes, et le BCD (vu au démarrage suivant).
        if (decoded.Value is RestartDeviceOp or RemoveDriverPackageOp or RestoreDriverPackageOp)
            return Deny("A device or driver-store op can never be a lease revert: it would act with nobody at the machine.");
        // Les droits d'un service se rendent sur le clic du joueur : par un bail, ils rouvriraient le fichier au compte sans
        // demande.
        if (decoded.Value is CorrigerDroitsDuServiceOp or RendreDroitsDuServiceOp)
            return Deny("A service permission op can never be a lease revert: it is the player's gesture only.");
        // La remise à l'usine de la carte AMD efface aussi ce que le joueur a réglé dans AMD Software : un geste, jamais un bail.
        if (decoded.Value is ApplyAdlxTuningOp { Kind: AdlxTuningKind.RemiseUsine })
            return Deny("A GPU factory reset can never be a lease revert: it is the player's gesture only.");
        if (decoded.Value is SetBcdValueOp or DeleteBcdValueOp or ScheduleMemoryDiagnosticOp or CancelMemoryDiagnosticOp)
            return Deny("A boot-configuration op can never be a lease revert: it would change the next boot unattended.");
        // Un ordre de bail n'est pas executable par l'executeur : en inverse, il echouerait et serait rejoue toutes les 5 s.
        if (decoded.Value is EstablishLeaseOp or RenewLeaseOp)
            return Deny("A lease op can never be a lease revert: the executor cannot run it.");
        // Un retour de bail rend l'origine : il n'a ni pose à marquer ni acquit à donner.
        if (decoded.Value is PoseEnAttenteDOrigineOp or AcquitterEcritureOp)
            return Deny("A write-ahead journal op can never be a lease revert.");

        var key = new LeaseKey(owner, leaseId);
        var expiry = _clock.UtcNow.AddSeconds(leaseSeconds);
        var entry = new LeaseEntry(decoded.Value, expiry);

        lock (_inscription)
        {
            if (_inversesEnCours.ContainsKey(leaseId))
                return Result.Fail(ErrorCodes.HelperOpFailed, Docteur.T("LW.RetourEnCours"));
            // AddOrUpdate : rétablir le même (owner, id) le rafraîchit. Remplacé avant que l'inverse le retire, l'ancien bail
            // n'est plus l'entrée que l'inverse attend : il ne part pas.
            _leases[key] = entry;
        }
        _rendus.TryRemove(key, out _);
        return Result.Ok();
    }

    /// <summary>Vrai si cet appelant tient un bail non échu dont le retour rend le ventilateur de cette carte à
    /// l'automatique : la garde thermique (80 °C) et l'expiration ne couvrent que les baux.</summary>
    public bool VentilateurSousBail(LeaseOwner owner, int gpuIndex)
        => _leases.Any(b => b.Key.Owner == owner && b.Value.Expiry > _clock.UtcNow
                            && b.Value.RevertOp is RestoreGpuFanAutoOp r && r.GpuIndex == gpuIndex);

    /// <summary>Battement de cœur : repousse l'échéance d'un bail existant. Seul le propriétaire renouvelle ; un
    /// renouvellement ne crée jamais de bail.</summary>
    public Result Renew(LeaseOwner owner, string leaseId, int leaseSeconds)
    {
        if (!owner.IsIdentified)
            return Deny("Lease owner is not an authenticated caller.");
        if (string.IsNullOrWhiteSpace(leaseId))
            return Deny("Lease id is required.");
        if (leaseSeconds <= 0)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease duration must be positive.");

        var key = new LeaseKey(owner, leaseId);
        // La clé inclut le propriétaire : un autre appelant ne trouve aucun bail.
        if (!_leases.TryGetValue(key, out var existing))
            return _rendus.ContainsKey(key)
                ? Result.Fail(ErrorCodes.LeaseReverted, "This helper reverted the lease: its original state is back.")
                : _inversesEnCours.ContainsKey(leaseId) ? EnCoursDeRendu()
                : Result.Fail(ErrorCodes.LeaseUnknown, "No such lease for this caller.");
        if (leaseSeconds > DureeMaxSecondes)
            return Result.Fail(ErrorCodes.NotAllowlisted, $"Lease duration above {DureeMaxSecondes} s.");

        var renewed = existing with { Expiry = _clock.UtcNow.AddSeconds(leaseSeconds) };
        // Remplacer seulement l'entrée observée : prise entre-temps par le chien de garde pour son inverse (au réveil de
        // veille, par exemple), le bail n'existe plus et Ok serait faux.
        return _leases.TryUpdate(key, renewed, existing) ? Result.Ok() : EnCoursDeRendu();
    }

    // Passager : le battement continue, et le suivant lit l'issue (rendu, ou bail remis après un inverse raté).
    private static Result EnCoursDeRendu()
        => Result.Fail(ErrorCodes.LeaseReverting, "The lease expired while renewing; its inverse is running.");

    /// <summary>Le contrôleur a rendu lui-même : le bail est abandonné, sans retour automatique. Seul le propriétaire
    /// clôt.</summary>
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

    /// <summary>Un passage de surveillance : lance le retour de chaque bail expiré, puis l'efface ; un retour raté garde le
    /// bail et se journalise. Ne lève jamais.</summary>
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

    /// <summary>L'arret du helper : rend tous les baux, expires ou non. Le registre vit en memoire : le helper parti, un
    /// plafond d'envoi pose pour une partie resterait jusqu'au redemarrage. Meme chemin que l'expiration.</summary>
    public async Task RendreToutAsync(CancellationToken ct)
    {
        foreach (var kvp in _leases)
            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Rend tout de suite chaque bail dont le retour répond à <paramref name="aRendre"/>, expiré ou non (la garde
    /// thermique du ventilateur). Même chemin que l'expiration : revalidation feuille-exacte, jamais d'exception ; le
    /// contrôleur l'apprend à son battement suivant (« rendu »).</summary>
    public async Task RendreSiAsync(Func<PrivilegedOp, bool> aRendre, string pourquoi, CancellationToken ct)
    {
        foreach (var kvp in _leases)
        {
            if (!aRendre(kvp.Value.RevertOp)) continue;
            // Journalisé à la première tentative : un retour raté est remis et retenté à chaque passage.
            if (kvp.Value.DernierEchec is null) _log.Change(LeaseTweakId(kvp.Key), "held", pourquoi, "DrDXT");
            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
        }
    }

    // L'inverse reste « en cours » jusqu'à son issue (rendu, ou bail remis) : un battement entre les deux n'apprend jamais
    // un « bail inconnu » qui le couperait. Compté : l'arrêt et un passage de surveillance peuvent viser le même bail.
    private async Task FireRevertAsync(LeaseKey key, LeaseEntry entry, CancellationToken ct)
    {
        lock (_inscription) _inversesEnCours.AddOrUpdate(key.LeaseId, 1, (_, n) => n + 1);
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
            // Défense en profondeur : revalider à la feuille près avant d'exécuter.
            var allowed = _allowlist.Validate(entry.RevertOp);
            if (!allowed.IsOk)
            {
                // Plus dans l'allowlist : abandonnée, jamais exécutée, et journalisée.
                _leases.TryRemove(key, out _);
                _log.Change(LeaseTweakId(key), "expired", "revert-rejected-not-allowlisted", "DrDXT");
                return;
            }

            // Le bail quitte le registre avant son inverse, seulement s'il est encore l'entrée expirée observée : un Renew
            // pendant l'inverse est refusé. Renouvelé ou clos depuis le balayage, il n'est plus à défaire.
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
            // Un exécuteur qui lève ne traverse jamais la frontière : bail remis, échec journalisé.
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
            // Bail remis (TryAdd : un bail rétabli entre-temps l'emporte), rejoué à chaque passage ; journalisé seulement
            // quand l'erreur change, le journal du helper n'a pas de borne.
            var echec = $"revert-failed:{result.Error?.Code}";
            _leases.TryAdd(key, entry with { DernierEchec = echec });
            if (echec != entry.DernierEchec) _log.Change(LeaseTweakId(key), "expired", echec, "DrDXT");
        }
    }

    private static string LeaseTweakId(LeaseKey key) => $"lease:{key.LeaseId}";

    private static Result Deny(string message) => Result.Fail(ErrorCodes.ElevationDenied, message);

    // Identité (owner, leaseId) : un appelant ne vise que ses propres baux.
    private readonly record struct LeaseKey(LeaseOwner Owner, string LeaseId);

    // Le retour et son échéance ; immuable, Renew en fait une copie.
    private sealed record LeaseEntry(PrivilegedOp RevertOp, DateTimeOffset Expiry, string? DernierEchec = null);
}

// -----------------------------------------------------------------------
// LeaseOwner : l'identité authentifiée propriétaire d'un bail, tirée de CallerInfo.ClientProcessId (pid du pair résolu
// par le pipe, déjà vérifié comme le DrDxt.App de confiance). Toute l'auth des baux ; pid non résolu → refusé.
// -----------------------------------------------------------------------
public readonly record struct LeaseOwner(int? ProcessId = null)
{
    public bool IsIdentified => ProcessId is { } pid && pid > 0;
}
