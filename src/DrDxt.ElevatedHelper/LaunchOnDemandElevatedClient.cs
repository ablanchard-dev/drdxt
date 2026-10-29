using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// LaunchOnDemandElevatedClient : l'IElevatedClient de l'App qui reporte l'UAC à la première op privilégiée (rien au
// démarrage), puis délègue au client à handshake (clé éphémère par connexion). Au premier ExecuteAsync, HelperLauncher
// s'assure qu'il tourne (une UAC, ou réutilisation). Un lancement raté (UAC refusée, helper qui ne démarre pas) rend un
// Fail propre, repris à l'op suivante : une invite fermée ne casse pas l'application. Les deux coutures sont injectées
// (testée sans rien lancer) ; l'étape tourne au plus une fois à la fois, verrouillée seulement après un succès.
public sealed class LaunchOnDemandElevatedClient : IElevatedClient
{
    private readonly Func<CancellationToken, Task<Result>> _ensureRunningAsync;
    private readonly IElevatedClient _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ensured;

    public LaunchOnDemandElevatedClient(
        Func<CancellationToken, Task<Result>> ensureRunningAsync, IElevatedClient inner)
    {
        _ensureRunningAsync = ensureRunningAsync ?? throw new ArgumentNullException(nameof(ensureRunningAsync));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Le client interne, qui ne lance jamais le helper.</summary>
    public IElevatedClient SansRelance => _inner;

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Sans geste du joueur, jamais de lancement : le helper déjà ouvert répond, sinon le refus se dit.
        if (SansGesteDuJoueur.Actif)
            return SansLancement(await _inner.ExecuteAsync(op, ct).ConfigureAwait(false));

        var ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        if (!ensure.IsOk)
            return ensure; // lancement raté → aucun canal (reprise possible à l'op suivante)

        var r = await _inner.ExecuteAsync(op, ct).ConfigureAwait(false);
        if (r.Error?.Code != ErrorCodes.HelperUnreachable) return r;

        // Le helper s'arrête seul après 15 minutes d'inactivité (SEC1) : relancé une fois sur ce geste, l'op rejouée.
        _ensured = false;
        ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        return ensure.IsOk ? await _inner.ExecuteAsync(op, ct).ConfigureAwait(false) : ensure;
    }

    /// <summary>Une op qui repond passe par le meme helper lance ; sans ce relais, elle tomberait sur le refus par defaut
    /// de IElevatedClient.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        if (SansGesteDuJoueur.Actif)
        {
            var sansGeste = await _inner.ReadAsync(op, ct).ConfigureAwait(false);
            return sansGeste.Error?.Code == ErrorCodes.HelperUnreachable
                ? Result<byte[]>.Fail(ErrorCodes.HelperUnreachable, Docteur.T("Elevation.SansGeste"))
                : sansGeste;
        }

        var ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        if (!ensure.IsOk)
            return Result<byte[]>.Fail(ensure.Error!.Value.Code, ensure.Error.Value.Message);

        var lu = await _inner.ReadAsync(op, ct).ConfigureAwait(false);
        if (lu.Error?.Code != ErrorCodes.HelperUnreachable) return lu;
        _ensured = false; // même raison que ExecuteAsync : le helper s'est arrêté pour inactivité
        ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        return ensure.IsOk ? await _inner.ReadAsync(op, ct).ConfigureAwait(false)
                           : Result<byte[]>.Fail(ensure.Error!.Value.Code, ensure.Error.Value.Message);
    }

    // Le helper ne tourne pas et personne n'a cliqué : le refus dit pourquoi rien n'a été fait, et ce qui le fera.
    private static Result SansLancement(Result r) => r.Error?.Code == ErrorCodes.HelperUnreachable
        ? Result.Fail(ErrorCodes.HelperUnreachable, Docteur.T("Elevation.SansGeste"))
        : r;

    // Lance l'étape « s'assurer qu'il tourne » au plus une fois avec succès. Un échec ne
    // verrouille rien : une op suivante peut réessayer (par exemple après une première UAC fermée).
    private async Task<Result> EnsureOnceAsync(CancellationToken ct)
    {
        if (_ensured)
            return Result.Ok();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured)
                return Result.Ok();

            var r = await _ensureRunningAsync(ct).ConfigureAwait(false);
            if (r.IsOk)
                _ensured = true;
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }
}
