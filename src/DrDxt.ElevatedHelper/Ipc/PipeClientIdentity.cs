using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.ElevatedHelper.Ipc;

// PipeClientIdentity : le process id du client connecté au pipe, par GetNamedPipeClientProcessId (le moyen documenté),
// pour le CallerInfo que vérifie l'authentificateur. Tout échec rend null → refus (fail-closed).
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
