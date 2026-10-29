using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// IPrivilegedOpExecutor : exécute une op privilégiée, atteinte seulement après HMAC → anti-rejeu → autorisation de
// l'appelant → codec → OpAllowlist.Validate. Le contrat ne prend qu'une PrivilegedOp (hiérarchie scellée) : aucune
// surcharge par chaîne de commande ou de chemin, aucun chemin d'exécution générique.
// -----------------------------------------------------------------------
public interface IPrivilegedOpExecutor
{
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct);

    /// <summary>La même exécution, avec le processus qui l'a demandée. Les deux entrées du helper appellent cette forme ;
    /// le journal d'écriture anticipée la redéfinit pour noter le demandeur. Par défaut, l'appelant est ignoré.</summary>
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct, DrDxt.ElevatedHelper.Ipc.CallerInfo appelant)
        => ExecuteAsync(op, ct);

    /// <summary>
    /// Exécute une op qui répond au lieu d'agir et rend ses octets, avec les mêmes gardes que
    /// <see cref="ExecuteAsync"/>. Par défaut : échec, pas un succès vide (« pas de canal » n'est pas « variable vide »).
    /// </summary>
    Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result<byte[]>.Fail(
            ErrorCodes.NotAllowlisted, "This executor has no read path."));
}
