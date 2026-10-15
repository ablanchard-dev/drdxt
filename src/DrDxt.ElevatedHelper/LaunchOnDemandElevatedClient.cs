using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// LaunchOnDemandElevatedClient : l'IElevatedClient de l'App qui reporte l'élévation UAC à la
// première op privilégiée (l'App ne demande donc jamais rien au démarrage), puis délègue au vrai
// client à handshake.
//
// Au premier ExecuteAsync, il lance l'étape « s'assurer qu'il tourne » (HelperLauncher : une UAC
// interactive, ou réutilisation s'il tourne déjà). Une fois réussie, chaque op (celle-ci et les
// suivantes) passe au client interne à handshake, qui négocie une clé de session éphémère par
// connexion. Un lancement raté (UAC refusée, helper qui ne démarre jamais) remonte en Fail propre
// et peut être repris à une op suivante : une seule invite fermée ne doit pas casser
// l'application pour de bon.
//
// Les deux coutures (s'assurer qu'il tourne + client interne) sont injectées : la barrière est
// entièrement testée sans rien lancer. Sûr en concurrence : l'étape tourne au plus une fois à la
// fois et ne se « verrouille » qu'après un succès.
// -----------------------------------------------------------------------
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

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        var ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        if (!ensure.IsOk)
            return ensure; // lancement raté → aucun canal (reprise possible à l'op suivante)

        var r = await _inner.ExecuteAsync(op, ct).ConfigureAwait(false);
        if (r.Error?.Code != ErrorCodes.HelperUnreachable) return r;

        // Le helper s'arrête seul après 15 minutes d'inactivité (SEC1). « Déjà lancé » ne
        // vaut plus : il est relancé une fois, sur ce geste, et l'op est rejouée.
        _ensured = false;
        ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        return ensure.IsOk ? await _inner.ExecuteAsync(op, ct).ConfigureAwait(false) : ensure;
    }

    /// <summary>Une op qui repond a besoin du meme helper lance que les autres. Sans ce
    /// relais, une lecture tomberait sur le refus par defaut de IElevatedClient au lieu
    /// d'atteindre le client interne : un canal cable de bout en bout, muet a l'avant-dernier
    /// maillon.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
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
