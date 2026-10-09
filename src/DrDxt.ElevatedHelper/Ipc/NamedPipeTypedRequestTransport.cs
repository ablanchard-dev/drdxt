using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Real named-pipe transport for a typed privileged request.
//
// Connects to the ACL'd server pipe, writes the framed request, reads the
// framed response. All transport faults are wrapped → a fail-clean
// PrivilegedResponse (IO_FAILURE); an exception never escapes.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NamedPipeTypedRequestTransport : ITypedRequestTransport
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    public NamedPipeTypedRequestTransport(string? pipeName = null, int connectTimeoutMs = 5000)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public async Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(_connectTimeoutMs, ct).ConfigureAwait(false);

            await WireCodec.WriteMessageAsync(client, request, ct).ConfigureAwait(false);
            var response = await WireCodec
                .ReadMessageAsync<PrivilegedResponse>(client, ct)
                .ConfigureAwait(false);

            return response ?? Fail(ErrorCodes.IoFailure, "No response from elevated helper.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return Fail(ErrorCodes.IoFailure, "Could not reach the elevated helper.");
        }
    }

    private static PrivilegedResponse Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Message = message };
}
