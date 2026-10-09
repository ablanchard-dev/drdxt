using System.Collections.Concurrent;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// LeaseWatchdog — the helper-side dead-man's-switch for HELD privileged tweaks.
//
// MOTIVATION: a held GPU clock offset (or power limit) left applied after the
// controlling process dies is a real hazard. The watchdog guarantees a held
// privileged tweak is never left applied if its controller stops checking in:
//   * Establish records the tweak's REVERT op + an expiry (now + leaseSeconds).
//   * Renew (heartbeat) pushes expiry out while the controller is alive.
//   * TickAsync (driven by a background monitor) runs the recorded revert for any
//     EXPIRED lease, then clears it.
//
// SECURITY (the helper is the #1 risk surface — every invariant is preserved):
//   * The revert is the codec-JSON of an existing typed PrivilegedOp leaf. It is
//     re-bound through the same bounded PrivilegedOpCodec (no Type.GetType, no
//     reflection) and re-validated leaf-exact through OpAllowlist both at register
//     time (reject a hostile/non-allowlisted revert up front) AND again at expiry
//     (defense in depth). It is then run via the existing IPrivilegedOpExecutor —
//     there is NO new generic privileged primitive.
//   * Leases are tied to the AUTHENTICATED caller identity (LeaseOwner, derived
//     from the pipe-resolved process id). Only the owner that established a lease
//     may renew or complete it; any other caller is denied (ELEVATION_DENIED). An
//     unidentified owner is denied (deny-by-default).
//   * State is IN-MEMORY only (no disk persistence — that would be attack
//     surface). If the helper itself dies, NVAPI offsets are volatile (a reboot
//     clears them) — the documented backstop, not something engineered around.
//   * Nothing throws across the boundary: a revert failure at expiry is logged via
//     IDiagnosticsLog, surfaces an error code, and is best-effort RETRIED on the
//     next tick (the lease is kept until a revert SUCCEEDS).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class LeaseWatchdog
{
    private readonly IClock _clock;
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;
    private readonly IDiagnosticsLog _log;

    // Keyed by (owner, leaseId). ConcurrentDictionary so the background tick and an
    // IPC-thread Establish/Renew/Complete never corrupt the registry.
    private readonly ConcurrentDictionary<LeaseKey, LeaseEntry> _leases = new();

    // REV6 (revue du 08/10/2026) : le nom d'un bail dont l'inverse est en train de partir. Un nouveau bail du même nom
    // (même réglage, quel que soit le client) est refusé pendant ce temps : sinon son écriture pouvait partir avant
    // l'inverse de l'ancien, qui la défaisait (l'écran disait « gardé », la carte était rendue).
    private readonly ConcurrentDictionary<string, byte> _inversesEnCours = new(StringComparer.Ordinal);

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

    /// <summary>Registers a held tweak's auto-revert. The revert is bound + validated
    /// leaf-exact NOW (a hostile / non-allowlisted revert is refused up front). Ties
    /// the lease to the authenticated <paramref name="owner"/>.</summary>
    /// <summary>SEC1 : le nombre de baux tenus (le helper ne s'arrête pas pour inactivité tant qu'il y en a).</summary>
    public int NombreDeBaux => _leases.Count;

    /// <summary>Revue de sécurité du 08/10 : un bail ne dure pas plus de 10 minutes par renouvellement, et un appelant n'en
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
            return Result.Fail(ErrorCodes.HelperOpFailed, "Le retour du bail précédent est en cours : réessaie dans un instant.");
        // REV6b (revue n° 2 du 09/10/2026) : un autre client (une App plantée puis relancée) tient encore ce réglage ; son
        // bail expirera et rendra l'origine. Accepter le nouveau laissait cet inverse défaire la pose qu'on affichait « gardée ».
        if (_leases.Keys.Any(k => k.LeaseId == leaseId && k.Owner != owner))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                "Un autre DrDXT tient encore ce réglage ; il le rend de lui-même sous deux minutes : réessaie ensuite.");

        // Bind through the same bounded codec (no type-name resolution) ...
        var decoded = PrivilegedOpCodec.Deserialize(revertOpJson);
        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease revert op could not be bound.");

        // ... and validate it leaf-exact, exactly like any privileged op.
        var allowed = _allowlist.Validate(decoded.Value);
        if (!allowed.IsOk)
            return allowed;

        // M3 lot 2 — un op FIRMWARE ne peut jamais être le revert d'un bail, même s'il passe
        // l'allowlist. Le déclencheur normal d'un dead-man's switch est la MORT du contrôleur :
        // le revert part donc précisément quand plus personne n'est devant la machine. Un
        // redémarrage vers le setup firmware y arriverait chez le client comme une machine qui
        // repart dans le BIOS toute seule, sans rien à l'écran pour l'expliquer.
        //
        // C'est le même invariant que l'omission de la couture côté ReconcilerHost, et il était
        // percé par ici : l'allowlist autorise l'op (à juste titre, pour un clic utilisateur),
        // et le registre de baux l'exécutait ensuite sans surveillance.
        if (decoded.Value is RebootToFirmwareSetupOp or CancelPendingFirmwareBootOp)
            return Deny("A firmware-boot op can never be a lease revert: it would fire with nobody at the machine.");
        // 24/09/2026, même invariant : une écriture BCD partie sans personne devant la machine ne se
        // verrait qu'au démarrage suivant. Les ops BCD restent des gestes du joueur, jamais d'un bail.
        if (decoded.Value is SetBcdValueOp or DeleteBcdValueOp)
            return Deny("A boot-configuration op can never be a lease revert: it would change the next boot unattended.");
        // 26/09/2026 : un ordre de bail n'est pas executable par l'executeur (le serveur le route vers ce
        // registre). En inverse, il echouait a chaque passage, remis et rejoue toutes les 5 s, sans fin.
        if (decoded.Value is EstablishLeaseOp or RenewLeaseOp)
            return Deny("A lease op can never be a lease revert: the executor cannot run it.");

        var key = new LeaseKey(owner, leaseId);
        var expiry = _clock.UtcNow.AddSeconds(leaseSeconds);
        var entry = new LeaseEntry(decoded.Value, expiry);

        // AddOrUpdate so re-establishing the same (owner, id) refreshes it.
        _leases[key] = entry;
        return Result.Ok();
    }

    /// <summary>Heartbeat — extends an existing lease's expiry to now + leaseSeconds.
    /// Only the OWNER may renew; an unknown lease (or a different caller) is denied —
    /// a renew never creates a lease.</summary>
    public Result Renew(LeaseOwner owner, string leaseId, int leaseSeconds)
    {
        if (!owner.IsIdentified)
            return Deny("Lease owner is not an authenticated caller.");
        if (string.IsNullOrWhiteSpace(leaseId))
            return Deny("Lease id is required.");
        if (leaseSeconds <= 0)
            return Result.Fail(ErrorCodes.NotAllowlisted, "Lease duration must be positive.");

        var key = new LeaseKey(owner, leaseId);
        // The key INCLUDES the owner, so a different caller simply finds no lease →
        // denied (it can never reach, let alone extend, another caller's lease).
        if (!_leases.TryGetValue(key, out var existing))
            return Deny("No such lease for this caller.");
        if (leaseSeconds > DureeMaxSecondes)
            return Result.Fail(ErrorCodes.NotAllowlisted, $"Lease duration above {DureeMaxSecondes} s.");

        var renewed = existing with { Expiry = _clock.UtcNow.AddSeconds(leaseSeconds) };
        // Only replace if it is still the same entry we observed (no lost update).
        _leases.TryUpdate(key, renewed, existing);
        return Result.Ok();
    }

    /// <summary>The controller reverted cleanly itself — drop the lease so the
    /// watchdog never auto-reverts. Only the owner may complete its own lease.</summary>
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

    /// <summary>One monitor pass: run the recorded revert for every EXPIRED lease,
    /// then clear it. On revert FAILURE the lease is KEPT (best-effort retry next
    /// tick) and the failure is journaled. Never throws.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        foreach (var kvp in _leases)
        {
            ct.ThrowIfCancellationRequested();
            if (kvp.Value.Expiry > now)
                continue; // not yet expired

            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>26/09/2026 — l'arret du helper : rend tous les baux encore actifs, expires ou non. Le registre vit
    /// en memoire ; une fois le helper parti, personne ne peut plus les renouveler ni les rendre, et un plafond
    /// d'envoi pose pour une partie restait jusqu'au redemarrage. Meme chemin que l'expiration (revalidation
    /// feuille-exacte, jamais d'exception).</summary>
    public async Task RendreToutAsync(CancellationToken ct)
    {
        foreach (var kvp in _leases)
            await FireRevertAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
    }

    private async Task FireRevertAsync(LeaseKey key, LeaseEntry entry, CancellationToken ct)
    {
        Result result;
        try
        {
            // Defense in depth: re-validate the recorded op leaf-exact before
            // executing — the allowlist is the authorization, not a one-time gate.
            var allowed = _allowlist.Validate(entry.RevertOp);
            if (!allowed.IsOk)
            {
                // A recorded op that is somehow no longer allowlisted: drop it (we
                // will never run a non-allowlisted op) and journal the anomaly.
                _leases.TryRemove(key, out _);
                _log.Change(LeaseTweakId(key), "expired", "revert-rejected-not-allowlisted", "DrDXT");
                return;
            }

            // 25/09/2026 : the lease leaves the registry BEFORE its inverse runs, and only if it is still the
            // expired entry we saw. A Renew that lands during the inverse is then denied (the controller
            // re-establishes from a known state) instead of answering Ok for a lease about to be dropped.
            // If it was renewed or completed since the scan, it is no longer ours to revert.
            _inversesEnCours[key.LeaseId] = 0;
            try
            {
                if (!_leases.TryRemove(new KeyValuePair<LeaseKey, LeaseEntry>(key, entry)))
                    return;

                result = await _executor.ExecuteAsync(entry.RevertOp, ct).ConfigureAwait(false);
            }
            finally { _inversesEnCours.TryRemove(key.LeaseId, out _); }
        }
        catch (OperationCanceledException)
        {
            _leases.TryAdd(key, entry); // not a revert failure: the next tick tries again
            throw; // cancellation is not a revert failure — propagate
        }
        catch (Exception ex)
        {
            // A throwing executor must never cross this boundary. Put the lease back so
            // the next tick retries; journal the failure.
            var echec = $"revert-threw:{ex.GetType().Name}";
            _leases.TryAdd(key, entry with { DernierEchec = echec });
            if (echec != entry.DernierEchec) _log.Change(LeaseTweakId(key), "expired", echec, "DrDXT");
            return;
        }

        if (result.IsOk)
        {
            // Reverted — already out of the registry, so it never fires again.
            _log.Change(LeaseTweakId(key), "expired", "auto-reverted", "DrDXT");
        }
        else
        {
            // Put the lease BACK (best-effort retry on the next tick) + journal the error.
            // TryAdd: if the controller re-established meanwhile, its new lease wins.
            // 26/09/2026 : rejoue a chaque passage (abandonner laisserait l'etat en place), mais journalise
            // seulement quand l'erreur change : le journal du helper n'a pas de borne.
            var echec = $"revert-failed:{result.Error?.Code}";
            _leases.TryAdd(key, entry with { DernierEchec = echec });
            if (echec != entry.DernierEchec) _log.Change(LeaseTweakId(key), "expired", echec, "DrDXT");
        }
    }

    private static string LeaseTweakId(LeaseKey key) => $"lease:{key.LeaseId}";

    private static Result Deny(string message) => Result.Fail(ErrorCodes.ElevationDenied, message);

    // (owner, leaseId) identity for the registry. Owner-scoped so a caller can only
    // ever address its own leases.
    private readonly record struct LeaseKey(LeaseOwner Owner, string LeaseId);

    // The recorded revert + when it must fire if not renewed. Immutable; Renew makes
    // a copy with a new Expiry.
    private sealed record LeaseEntry(PrivilegedOp RevertOp, DateTimeOffset Expiry, string? DernierEchec = null);
}

// -----------------------------------------------------------------------
// LeaseOwner — the authenticated identity that owns a lease.
//
// Derived from CallerInfo.ClientProcessId (the pipe-resolved peer pid that the
// caller authenticator already verified is the trusted DrDxt.App). Tying the lease
// to this identity is the auth story: only the same authenticated caller can renew
// or complete the lease it established. An unresolved pid → unidentified → denied.
// -----------------------------------------------------------------------
public readonly record struct LeaseOwner(int? ProcessId = null)
{
    public bool IsIdentified => ProcessId is { } pid && pid > 0;
}
