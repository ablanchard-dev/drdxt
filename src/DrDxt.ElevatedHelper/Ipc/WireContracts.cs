using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Wire contracts for the elevated-helper IPC (named pipe).
//
// SECURITY (master plan §9.1/§9.13/§9.14):
//   * The wire carries a CLOSED discriminator (OpKind), never a type name.
//     The server maps it through a closed switch — there is NO
//     Type.GetType / polymorphic-by-name deserialization. A hostile payload
//     can therefore never coerce the helper into instantiating an arbitrary
//     type or reaching an un-allowlisted code path.
//   * OpKind is the on-wire mirror of the PrivilegedOp leaf allowlist.
//     In M0 the only member is Ping (a no-op health check). New members are
//     added (here AND in the closed dispatch switch) when M2 wires real ops.
// -----------------------------------------------------------------------

/// <summary>
/// Closed set of operations the helper will dispatch. Grows only by adding a
/// new member here together with a matching arm in the server's closed switch.
/// Any value not in this set is rejected fail-closed (OP_NOT_ALLOWED).
/// </summary>
public enum OpKind
{
    Ping = 0,
}

/// <summary>
/// Request envelope sent client -> server. The HMAC authenticates the canonical
/// bytes of all the OTHER fields (see <see cref="RequestHmac"/>). The nonce
/// gives per-request uniqueness for anti-replay.
/// </summary>
public sealed record PrivilegedRequest
{
    public OpKind Kind { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public string HmacBase64 { get; init; } = string.Empty;
}

/// <summary>
/// Response envelope sent server -> client. Carries a coarse Ok/ErrorCode;
/// never leaks internal exception detail across the trust boundary.
/// </summary>
public sealed record PrivilegedResponse
{
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Le canal de retour de donnees. Rempli uniquement par les ops qui repondent au lieu
    /// d'agir — leurs arms sont nommés un par un dans le switch de dispatch de
    /// <c>TypedPrivilegedServer</c>. Toutes les autres ops laissent ce champ null et leur réponse reste
    /// Octet pour octet celle d'avant : <see cref="JsonIgnoreCondition.WhenWritingNull"/>
    /// l'omet du JSON, et <c>WireCodec</c> refuse fail-closed tout membre inconnu.
    ///
    /// Sans lui, une lecture firmware n'apprenait que « la variable est lisible », jamais sa
    /// valeur — donc aucune lecture ne pouvait atteindre l'utilisateur.
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
    /// charge utile n'est pas une lecture » se serait cassée d'un côté sans qu'aucun test du
    /// côté d'en face ne rougisse.
    ///
    /// Les trois cas sont distincts et le restent :
    ///   * échec → le code et le motif du serveur, verbatim ;
    ///   * Ok sans charge → echec. C'est ce que rend une op qui n'ANSWER pas ; rendre un
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
