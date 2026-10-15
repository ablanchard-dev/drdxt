using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// InProcessElevatedClient : l'exécuteur privilégié en processus du réconciliateur de démarrage.
//
// Branchement du moat (option B) : l'hôte réconciliateur
// tourne en LocalSystem et réapplique au démarrage des leviers validés, sans aucun IPC. Il n'y a
// aucun service helper élevé où se connecter ni aucun point d'entrée privilégié ouvert : la seule
// entrée qui puisse mener une écriture est le manifeste vérifié par signature Ed25519, dont l'ACL
// réserve l'écriture aux administrateurs, chargé en amont par SignedDesiredStateStore (signature
// vérifiée avant l'analyse, fail-closed).
//
// Ce client est la dernière barrière. Il fait tourner la même OpAllowlist à la feuille près que
// le TypedPrivilegedServer distant applique en deux dernières étapes (allowlist -> exécution),
// puis aiguille vers le même PrivilegedOpExecutor. Les barrières de transport qu'il omet (intégrité
// HMAC, nonce anti-rejeu, autorisation de l'appelant) n'existent que pour authentifier un appelant
// distant sur le pipe ; en processus il n'y a pas d'appelant distant, elles ne s'appliquent donc
// pas. L'allowlist est la vraie autorisation : la repasser ici garantit que même un bug de logique
// dans la boucle de réconciliation ne peut jamais mener une op hors allowlist. L'exécuteur emballe
// chaque panne native -> Result : une action en échec ne lève jamais hors d'ici.
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
        // Défense n°1 : allowlist à la feuille près avant toute action. Une op refusée n'atteint
        // jamais l'exécuteur (aucune action partielle, aucun contact avec l'OS).
        var allowed = _allowlist.Validate(op);
        if (!allowed.IsOk)
            return allowed;

        return await _executor.ExecuteAsync(op, ct).ConfigureAwait(false);
    }
}
