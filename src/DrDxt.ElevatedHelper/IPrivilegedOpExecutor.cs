using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// IPrivilegedOpExecutor — performs an ALREADY-ALLOWLISTED privileged op.
//
// SECURITY: this seam is reached only after a request has passed, in order,
// HMAC integrity → anti-replay → caller authorization → codec binding →
// OpAllowlist.Validate. It therefore receives a typed, closed PrivilegedOp leaf
// that is known-good. The contract takes a PrivilegedOp (the sealed hierarchy)
// and nothing else — no string command/path overload exists, so there is no
// generic exec path. The concrete executor (registry/power/NVAPI/ADLX/service)
// lands in Phases D/E/F; M2 Phase A only proves the gate.
// -----------------------------------------------------------------------
public interface IPrivilegedOpExecutor
{
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct);

    /// <summary>
    /// Exécute une op qui REPOND au lieu d'agir, et rend ses octets. Même contrat de sécurité
    /// que <see cref="ExecuteAsync"/> : la feuille est déjà passée par HMAC → anti-rejeu →
    /// autorisation d'appelant → codec borné → allowlist. Le seul écart est le retour.
    ///
    /// Implémentation par défaut : echec. Un exécuteur qui n'a pas de chemin de lecture le DIT
    /// au lieu de rendre un succès vide — « je n'ai pas de canal » et « la variable est vide »
    /// ne sont pas le même état. Les doublures de test héritent donc du bon refus sans changer.
    /// </summary>
    Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result<byte[]>.Fail(
            ErrorCodes.NotAllowlisted, "This executor has no read path."));
}
