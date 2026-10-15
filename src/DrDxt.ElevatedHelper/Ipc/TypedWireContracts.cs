namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Enveloppe de fil pour une op privilégiée typée.
//
// Sécurité : le fil porte le JSON du codec d'une feuille PrivilegedOp fermée (OpJson), pas un
// nom de type. Le serveur ne la relie que par le switch de discriminant borné de
// PrivilegedOpCodec, puis la valide à la feuille près (OpAllowlist) avant de l'exécuter. Le HMAC
// authentifie les octets canoniques de (OpJson, Nonce) ; le nonce donne l'unicité anti-rejeu
// par requête.
// -----------------------------------------------------------------------
public sealed record TypedPrivilegedRequest
{
    /// <summary>Le JSON PrivilegedOpCodec d'une feuille PrivilegedOp fermée.</summary>
    public string OpJson { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string HmacBase64 { get; init; } = string.Empty;
}
