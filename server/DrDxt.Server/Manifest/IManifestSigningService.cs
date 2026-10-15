using DrDxt.Server.Common;

namespace DrDxt.Server.Manifest;

// SERVER-SIDE CUSTODY of the manifest-signing PRIVATE key. The reconciler moat reapplies a
// SIGNED desired-state manifest; the signature is the integrity root. The PRIVATE key lives
// ONLY here (read from secure config/env — never a hardcoded literal, never in a client/service
// binary). The client holds only the matching PUBLIC key and VERIFIES: the signer stays off the client.
public interface IManifestSigningService
{
    // Produces a detached Ed25519 signature (base64) over the EXACT canonical manifest bytes
    // the client will verify. Fail-closed: no configured key / empty input / crypto error ⇒
    // Fail, never throw, never fail-open.
    Result<string> Sign(byte[] canonicalBytes);
}
