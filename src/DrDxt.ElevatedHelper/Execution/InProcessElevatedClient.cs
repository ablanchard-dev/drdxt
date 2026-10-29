using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// InProcessElevatedClient : l'exécuteur privilégié en processus du réconciliateur de démarrage.
//
// L'hôte réconciliateur (option B du moat) tourne en LocalSystem et réapplique au démarrage, sans IPC ni point d'entrée
// privilégié ouvert : la seule entrée est le manifeste signé Ed25519 (ACL d'écriture admin), vérifié avant l'analyse
// par SignedDesiredStateStore (fail-closed).
// Dernière barrière : la même OpAllowlist à la feuille près que TypedPrivilegedServer, puis le même
// PrivilegedOpExecutor. HMAC, anti-rejeu et autorisation de l'appelant authentifient un pair distant, absent ici ;
// l'allowlist repassée garantit qu'un bug de la boucle ne mène jamais une op hors allowlist. Jamais d'exception.
// -----------------------------------------------------------------------
public sealed class InProcessElevatedClient : IElevatedClient
{
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;

    public InProcessElevatedClient(OpAllowlist allowlist, IPrivilegedOpExecutor executor)
    {
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Allowlist à la feuille près d'abord : une op refusée n'atteint jamais l'exécuteur.
        var allowed = _allowlist.Validate(op);
        if (!allowed.IsOk)
            return allowed;

        // Le demandeur est ce processus même (le service, la désinstallation).
        return await _executor.ExecuteAsync(op, ct, new DrDxt.ElevatedHelper.Ipc.CallerInfo(Environment.ProcessId)).ConfigureAwait(false);
    }
}
