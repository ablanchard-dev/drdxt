namespace DrDxt.ElevatedHelper.Ipc;

// Enveloppe de fil d'une op privilégiée typée : le JSON du codec d'une feuille PrivilegedOp fermée (OpJson), pas un nom
// de type. Le serveur ne la relie que par le switch de discriminant borné de PrivilegedOpCodec, puis la valide à la
// feuille près (OpAllowlist). Le HMAC authentifie (OpJson, Nonce) ; le nonce rend chaque requête unique (anti-rejeu).
public sealed record TypedPrivilegedRequest
{
    /// <summary>Le JSON PrivilegedOpCodec d'une feuille PrivilegedOp fermée.</summary>
    public string OpJson { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string HmacBase64 { get; init; } = string.Empty;
}
