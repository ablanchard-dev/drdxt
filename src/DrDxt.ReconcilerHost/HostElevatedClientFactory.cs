using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostElevatedClientFactory — builds the SERVICE-context IElevatedClient.
//
// Mirrors the App's ElevatedClientFactory but for the background service: when a
// per-session HMAC key is provisioned (base64, >=32 bytes), the real
// TypedNamedPipeElevatedClient over the ACL'd named pipe is returned; otherwise a
// fail-clean UNAVAILABLE client is returned that refuses every op (Result.Fail) so a
// reconcile attempt logs "elevation unavailable" and fails clean — never crashes, and
// Never fakes a successful elevation.
//
// Deferred integration point (flagged in the report): how a BACKGROUND SERVICE
// launches/authenticates to the elevated helper without an interactive UAC prompt is a
// known, not-yet-established handshake. Until it lands, no key is provisioned in the
// service context and the factory returns the unavailable client by design — exactly
// like the App path was wired then smoke-tested separately. We wire the real client
// behind the seam; we do not fabricate a session.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostElevatedClientFactory
{
    /// <summary>The env var carrying the base64 per-session HMAC key (same contract as
    /// the App side). In the service context this is set by whatever establishes the
    /// helper handshake; until that exists it is unset → unavailable client.</summary>
    public const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";

    /// <summary>Builds the client. <paramref name="keySource"/> reads the raw base64
    /// session key (injected so the fail-clean path is unit-tested without touching the
    /// real environment); production passes the env reader.</summary>
    public static IElevatedClient Create(Func<string?> keySource)
    {
        var key = ParseKey(keySource());
        if (key is null)
            return new UnavailableElevatedClient();

        var transport = new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName);
        return new TypedNamedPipeElevatedClient(key, transport);
    }

    /// <summary>Production overload: reads the session key from the environment.</summary>
    public static IElevatedClient Create()
        => Create(() => Environment.GetEnvironmentVariable(SessionKeyEnv));

    /// <summary>Builds the reconciler→helper CONNECT-only client (M5 channel).
    ///
    /// The low-priv reconciler service does not launch the helper (no UAC at boot): it
    /// only CONNECTS to the already-running helper SERVICE over the same §9.13 handshake
    /// the App uses, negotiating an ephemeral per-session key on connect. So this is the
    /// bare <see cref="HandshakingElevatedClient"/> — not the App's launch-on-demand
    /// wrapper (which would raise a UAC). It is connect-only by construction (it never
    /// spawns anything).
    ///
    /// FAIL-CLEAN when the helper is absent: <see cref="HandshakingElevatedClient"/>
    /// already wraps every transport fault → a clean Result. When no helper service is
    /// listening, its bounded connect-retry deadline elapses and it returns
    /// HELPER_UNREACHABLE (no crash) — the reconciler then logs "elevation unavailable"
    /// and does not reapply. The handshake also re-checks caller-auth server-side, so an
    /// un-trusted reconciler image is denied there (defense in depth).</summary>
    /// <param name="pipeName">Override for tests; defaults to the standard ACL'd pipe.</param>
    /// <param name="connectTimeoutMs">Connect-retry deadline; the unit test uses a short
    /// value so the absent-helper fail-clean path returns promptly.</param>
    /// <param name="expectedHelperImagePath">Le chemin du helper que ce client accepte
    /// d'ecouter. Par defaut celui pose a cote du service (meme source de verite que l'App).
    /// S'il est introuvable, la resolution rend null et le client refuse toute session plutot
    /// que de faire confiance a un tube quelconque — c'est le sens fail-closed de la garde.</param>
    public static IElevatedClient CreateConnectOnly(
        string? pipeName = null, int connectTimeoutMs = 5000, string? expectedHelperImagePath = null)
        => new HandshakingElevatedClient(
            pipeName ?? PipeSecurityFactory.DefaultPipeName,
            expectedHelperImagePath ?? PipeServerIdentity.TryResolveExpectedHelperPath(),
            connectTimeoutMs);

    private static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Fail-clean client used when no helper session is provisioned (the
    /// service-context handshake is not yet established): refuses every op so a
    /// reconcile fails clean rather than crashing. Never reports a faked success.</summary>
    private sealed class UnavailableElevatedClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.ElevationDenied,
                "Elevation unavailable: the reconciler service has no provisioned helper session."));
    }
}
