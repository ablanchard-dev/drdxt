using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// PipeClientIdentity — resolves the connected pipe client's process id.
//
// SECURITY: the server uses this (after a connection is accepted) to build the
// CallerInfo the authenticator checks. GetNamedPipeClientProcessId is the
// documented way to attribute a pipe connection to a process. Any failure
// returns null → the authenticator denies (fail-closed).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeClientIdentity
{
    public static int? TryGetClientProcessId(NamedPipeServerStream server)
    {
        if (server is null || !server.IsConnected)
            return null;
        try
        {
            var handle = server.SafePipeHandle.DangerousGetHandle();
            return GetNamedPipeClientProcessId(handle, out var pid) && pid != 0
                ? unchecked((int)pid)
                : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
