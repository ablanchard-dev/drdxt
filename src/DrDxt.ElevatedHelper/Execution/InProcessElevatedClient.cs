using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// InProcessElevatedClient — the boot reconciler's IN-PROCESS privileged executor.
//
// MOAT WIRING (decision M4/M5-MOAT-WIRING, Option B): the reconciler host runs as
// LocalSystem and reapplies validated tweaks at boot without any IPC. There is no
// elevated helper service to connect to and no open privileged endpoint — the only
// input that can drive a write is the Ed25519-signature-verified, admin-write-ACL'd
// manifest, loaded upstream by the SignedDesiredStateStore (signature checked before
// parse, fail-closed).
//
// This client is the LAST gate. It runs the same leaf-exact OpAllowlist the remote
// TypedPrivilegedServer applies as its final two steps (allowlist -> execute), then
// dispatches to the same PrivilegedOpExecutor. The transport gates it omits (HMAC
// integrity, anti-replay nonce, caller authorization) exist only to authenticate a
// REMOTE caller over the pipe; in-process there is no remote caller, so they are not
// applicable. The allowlist is the real authorization (master plan section 9.13), so
// re-running it here means even a logic bug in the reconcile loop can never drive a
// non-allowlisted op. The executor wraps every native fault -> Result, so a failing
// action never throws out of here.
// -----------------------------------------------------------------------
public sealed class InProcessElevatedClient : IElevatedClient
{
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;

    public InProcessElevatedClient(OpAllowlist allowlist, IPrivilegedOpExecutor executor)
    {
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Defense n.1: leaf-exact allowlist before any action. A denied op never reaches
        // the executor (no partial action, no OS touch).
        var allowed = _allowlist.Validate(op);
        if (!allowed.IsOk)
            return allowed;

        return await _executor.ExecuteAsync(op, ct).ConfigureAwait(false);
    }
}
