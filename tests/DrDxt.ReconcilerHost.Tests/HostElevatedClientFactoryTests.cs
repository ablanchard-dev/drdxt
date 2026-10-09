using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostElevatedClientFactory — builds the SERVICE-context IElevatedClient.
//
//   * a provisioned session key (>=32 bytes, base64) → the real
//     TypedNamedPipeElevatedClient over the ACL'd pipe.
//   * NO key (the service-context helper handshake is not yet established) → a
//     fail-clean UNAVAILABLE client that refuses every op (Result.Fail), so a
//     reconcile attempt logs "elevation unavailable" and fails clean, never crashes.
//   * we never fake a successful elevation: the unavailable client always Fails.
// -----------------------------------------------------------------------
public sealed class HostElevatedClientFactoryTests
{
    private static readonly PrivilegedOp AnyOp = new PingOp();

    [Fact]
    public async Task NoSessionKey_ReturnsFailCleanUnavailableClient_ThatRefusesEveryOp()
    {
        var client = HostElevatedClientFactory.Create(keySource: () => null);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse("no session provisioned → fail-clean, never a faked success");
        result.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public async Task EmptyOrTooShortKey_TreatedAsUnavailable()
    {
        var tooShort = Convert.ToBase64String(new byte[8]);
        var client = HostElevatedClientFactory.Create(keySource: () => tooShort);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void ValidKey_ReturnsTheRealTypedPipeClient()
    {
        var key = Convert.ToBase64String(new byte[32]); // 32 bytes minimum.
        var client = HostElevatedClientFactory.Create(keySource: () => key);

        // We do not connect a pipe here (no helper in a unit test); we only assert the
        // factory wires the real client type when a key is present — the actual IPC is
        // smoke-tested separately (the service-context handshake is a deferred edge).
        client.Should().BeOfType<TypedNamedPipeElevatedClient>();
    }
}
