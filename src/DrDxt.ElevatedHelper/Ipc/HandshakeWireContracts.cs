namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Bootstrap handshake wire contracts (§9.13 — ephemeral session-key negotiation).
//
// This is the CONNECT-time handshake that runs before the existing typed,
// HMAC-keyed protocol. Its only job is to establish the per-session HMAC key
// that the proven TypedPrivilegedServer / TypedNamedPipeElevatedClient layer
// then uses unchanged.
//
// Order on the wire (one round trip, framed by WireCodec like every other
// message — length-prefixed JSON, bounded, unknown members rejected):
//   1. client -> server : HandshakeHello (protocol marker only; no secret)
//   2. server authenticates the caller (PID -> image identity) before replying
//   3a. caller OK  -> server -> client : HandshakeResult{ Ok=true,  SessionKeyBase64 }
//   3b. caller BAD -> server -> client : HandshakeResult{ Ok=false } (NO key) then close
//
// SECURITY:
//   * The key is generated SERVER-SIDE (the elevated, trusted end) with a CSPRNG
//     and is EPHEMERAL (per connection, in memory only — never persisted).
//   * The key is only ever sent over the LOCAL, DACL-protected, FIRST_PIPE_INSTANCE
//     pipe AND only after the caller has been authenticated. A non-DrDxt.App peer
//     never receives a key (it is denied before 3a).
//   * The Hello carries NO secret; it cannot be replayed to extract a key because
//     a key is only minted after caller-auth, which an attacker process cannot pass.
// -----------------------------------------------------------------------

/// <summary>Client -> server connect greeting. Carries only a protocol-version
/// marker so the server can reject an incompatible/garbage peer fail-clean. No
/// secret travels here.</summary>
public sealed record HandshakeHello
{
    /// <summary>Closed protocol marker. Bumped if the handshake shape ever changes.</summary>
    public string Protocol { get; init; } = HandshakeProtocol.Version;
}

/// <summary>Server -> client handshake outcome. On success it carries the freshly
/// minted ephemeral session key (base64, 32 bytes). On failure it carries NO key
/// (the client must fail-closed and obtain no channel).</summary>
public sealed record HandshakeResult
{
    public bool Ok { get; init; }
    /// <summary>Base64 of the 32-byte ephemeral session key. Present only when
    /// <see cref="Ok"/> is true and the caller was authenticated.</summary>
    public string? SessionKeyBase64 { get; init; }
}

/// <summary>Handshake protocol constants (single source of truth for the marker
/// the client sends and the server checks).</summary>
public static class HandshakeProtocol
{
    public const string Version = "drdxt-handshake-v1";

    /// <summary>Ephemeral session-key length in bytes (seeds HMAC-SHA256).</summary>
    public const int SessionKeyBytes = 32;
}
