using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// Enveloppe de réponse de l'IPC du helper élevé (pipe nommé). Les requêtes voyagent en
// TypedPrivilegedRequest (TypedWireContracts.cs).

/// <summary>
/// Enveloppe de réponse envoyée serveur -> client. Porte un Ok/ErrorCode grossier ;
/// ne laisse jamais fuir le détail d'une exception interne à travers la frontière de confiance.
/// </summary>
public sealed record PrivilegedResponse
{
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Le canal de retour de donnees. Rempli uniquement par les ops qui repondent au lieu
    /// d'agir : leurs branches sont nommées une par une dans le switch de dispatch de
    /// <c>TypedPrivilegedServer</c>. Toutes les autres ops laissent ce champ null et leur réponse reste
    /// octet pour octet la même : <see cref="JsonIgnoreCondition.WhenWritingNull"/>
    /// l'omet du JSON, et <c>WireCodec</c> refuse fail-closed tout membre inconnu.
    ///
    /// Sans lui, une lecture firmware n'apprendrait que « la variable est lisible », jamais sa
    /// valeur : aucune lecture ne pourrait donc atteindre l'utilisateur.
    ///
    /// Borne : la charge est plafonnée à <see cref="MaxDataBytes"/> octets avant encodage,
    /// bien en dessous de <c>WireCodec.MaxMessageBytes</c> même après base64 (+33 %). Un
    /// dépassement est un echec, jamais une troncature silencieuse : rendre la moitié d'une
    /// variable firmware serait pire que ne rien rendre.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataBase64 { get; init; }

    /// <summary>Plafond de la charge utile brute (avant base64) qu'une réponse peut porter.</summary>
    public const int MaxDataBytes = 16 * 1024;

    /// <summary>
    /// Lit cette réponse comme une lecture. Vit ici, en un seul endroit, parce que les deux
    /// clients (pipe nommé et handshake) en ont besoin : dupliquée, la règle « un Ok sans
    /// charge utile n'est pas une lecture » se casserait d'un côté sans qu'aucun test du
    /// côté d'en face ne rougisse.
    ///
    /// Les trois cas sont distincts et le restent :
    ///   * échec → le code et le motif du serveur, mot pour mot ;
    ///   * Ok sans charge → echec. C'est ce que rend une op qui ne répond pas ; rendre un
    ///     tableau vide le ferait lire comme « le firmware a rendu zéro octet » ;
    ///   * base64 illisible → echec, jamais un tableau partiel.
    /// </summary>
    public Result<byte[]> ToBytes()
    {
        if (!Ok)
            return Result<byte[]>.Fail(ErrorCode ?? ErrorCodes.IoFailure, Message ?? "Privileged read failed.");

        if (DataBase64 is null)
            return Result<byte[]>.Fail(ErrorCodes.IoFailure,
                "The helper answered OK but carried no data (this op does not return data).");

        try
        {
            return Result<byte[]>.Ok(Convert.FromBase64String(DataBase64));
        }
        catch (FormatException)
        {
            return Result<byte[]>.Fail(ErrorCodes.IoFailure, "The helper returned an unreadable payload.");
        }
    }
}
