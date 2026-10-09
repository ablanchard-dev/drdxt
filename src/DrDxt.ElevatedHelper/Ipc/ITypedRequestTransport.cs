namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// ITypedRequestTransport — the wire seam for a signed typed request.
//
// Separates the client's serialize+sign logic (TypedNamedPipeElevatedClient)
// from the actual transport (named pipe). Tests inject an in-memory loopback to
// a real TypedPrivilegedServer, exercising the full fail-clean round-trip with
// fakes — no real pipe, nothing elevated.
// -----------------------------------------------------------------------
public interface ITypedRequestTransport
{
    Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct);
}
