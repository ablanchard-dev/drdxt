using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostElevatedClientFactory.CreateConnectOnly — the reconciler→helper client.
//
// The low-priv reconciler service does not launch the helper (no UAC at boot): it
// only CONNECTS to the already-running helper service over the same §9.13 handshake
// the App uses, with the reconciler-host exe as its caller identity. So the
// reconciler-side client is "connect-only" — a HandshakingElevatedClient with NO
// runas launch wrapper.
//
//   * It returns the real handshaking client (connect-only), never the
//     launch-on-demand wrapper.
//   * When the helper service is absent, the client fails CLEAN (HELPER_UNREACHABLE)
//     rather than crashing — the reconciler logs "elevation unavailable" and does not
//     reapply. We assert this with a NON-existent pipe + a short connect timeout.
// -----------------------------------------------------------------------
public sealed class HostConnectOnlyElevatedClientTests
{
    private static readonly PrivilegedOp AnyOp = new PingOp();

    [Fact]
    public void CreateConnectOnly_returns_the_real_connect_only_handshaking_client()
    {
        var client = HostElevatedClientFactory.CreateConnectOnly();
        // It is the connect-only handshaking client — not the launch-on-demand wrapper
        // (the reconciler must never trigger a UAC at boot).
        client.Should().BeOfType<HandshakingElevatedClient>();
    }

    [Fact]
    public async Task When_the_helper_is_absent_the_connect_only_client_fails_clean()
    {
        // A unique, never-served pipe + a short connect deadline → the client cannot
        // reach a helper and must fail clean (no crash), surfacing HELPER_UNREACHABLE.
        var deadPipe = "DrDXT.ReconConnectOnly." + Guid.NewGuid().ToString("N");
        var client = HostElevatedClientFactory.CreateConnectOnly(
            pipeName: deadPipe, connectTimeoutMs: 250);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse("no helper service is listening → fail clean, never crash");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable);
    }
}
