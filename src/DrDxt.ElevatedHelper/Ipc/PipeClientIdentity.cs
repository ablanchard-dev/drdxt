using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// PipeClientIdentity : résout le process id du client connecté au pipe.
//
// Sécurité : le serveur s'en sert (après l'acceptation d'une connexion) pour construire le
// CallerInfo que vérifie l'authentificateur. GetNamedPipeClientProcessId est le moyen documenté
// d'attribuer une connexion de pipe à un processus. Tout échec rend null → l'authentificateur
// refuse (fail-closed).
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
