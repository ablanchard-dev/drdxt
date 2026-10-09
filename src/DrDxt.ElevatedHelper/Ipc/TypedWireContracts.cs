namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Wire envelope for a TYPED privileged op (M2 A3).
//
// SECURITY: the wire carries the codec JSON of a closed PrivilegedOp leaf
// (OpJson) — not a type name. The server binds it only via PrivilegedOpCodec's
// bounded discriminant switch, then validates it leaf-exact (OpAllowlist) before
// executing. The HMAC authenticates the canonical bytes of (OpJson, Nonce); the
// nonce gives per-request anti-replay uniqueness.
// -----------------------------------------------------------------------
public sealed record TypedPrivilegedRequest
{
    /// <summary>The PrivilegedOpCodec JSON of a closed PrivilegedOp leaf.</summary>
    public string OpJson { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string HmacBase64 { get; init; } = string.Empty;
}
