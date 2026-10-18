using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// IPrivilegedOpExecutor : exécute une op privilégiée déjà passée par l'allowlist.
//
// Sécurité : cette couture n'est atteinte qu'après qu'une requête a passé, dans l'ordre,
// intégrité HMAC → anti-rejeu → autorisation de l'appelant → liaison par le codec →
// OpAllowlist.Validate. Elle reçoit donc une feuille PrivilegedOp typée, fermée, reconnue
// bonne. Le contrat prend une PrivilegedOp (la hiérarchie scellée) et rien d'autre : aucune
// surcharge par chaîne de commande ou de chemin n'existe, donc aucun chemin d'exécution
// générique. L'exécuteur concret (registre/alimentation/NVAPI/ADLX/service) arrive avec chaque
// levier ; cette couche ne prouve que la barrière.
// -----------------------------------------------------------------------
public interface IPrivilegedOpExecutor
{
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct);

    /// <summary>La même exécution, avec le processus qui l'a demandée. Les deux entrées du helper appellent cette forme ;
    /// le journal d'écriture anticipée la redéfinit pour noter le demandeur. Par défaut, l'appelant est ignoré.</summary>
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct, DrDxt.ElevatedHelper.Ipc.CallerInfo appelant)
        => ExecuteAsync(op, ct);

    /// <summary>
    /// Exécute une op qui répond au lieu d'agir, et rend ses octets. Même contrat de sécurité
    /// que <see cref="ExecuteAsync"/> : la feuille est déjà passée par HMAC → anti-rejeu →
    /// autorisation d'appelant → codec borné → allowlist. Le seul écart est le retour.
    ///
    /// Implémentation par défaut : echec. Un exécuteur qui n'a pas de chemin de lecture le dit
    /// au lieu de rendre un succès vide : « je n'ai pas de canal » et « la variable est vide »
    /// ne sont pas le même état. Les doublures de test héritent donc du bon refus sans changer.
    /// </summary>
    Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result<byte[]>.Fail(
            ErrorCodes.NotAllowlisted, "This executor has no read path."));
}
