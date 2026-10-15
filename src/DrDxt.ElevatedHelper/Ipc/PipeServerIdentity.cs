using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// PipeServerIdentity : la moitie symetrique de PipeClientIdentity.
//
// Le serveur resout qui l'appelle (GetNamedPipeClientProcessId →
// chemin + SHA-256, DrDxtAppCallerAuthenticator). Ce fichier donne au client la
// meme capacite : resoudre qui lui repond sur le tube, pour qu'un processus non
// privilegie qui a pris le nom du tube avant le helper ne puisse plus se faire
// passer pour lui.
//
// Pourquoi OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageNameW
// et pas Process.MainModule comme cote serveur : le sens de la traversee est inverse.
// Le serveur (eleve) lit une App a integrite moyenne : MainModule passe. Le client
// (integrite moyenne) doit lire un helper eleve : MainModule y echoue, la lecture du
// module exigeant PROCESS_VM_READ. PROCESS_QUERY_LIMITED_INFORMATION existe exactement
// pour ce cas : il ne donne que le nom de l'image, jamais la memoire. Meme motif que
// DrDxt.App/Platform/GameLocator.cs, qui lit deja des processus qu'il ne peut pas ouvrir.
//
// Tout echec rend null → le client refuse (fail-closed, jamais fail-open).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeServerIdentity
{
    /// <summary>Nom de l'image du helper eleve. Source de verite unique : c'est ce
    /// fichier que l'App lance (ElevatedClientFactory) et c'est donc ce fichier que le
    /// client doit retrouver en face de lui sur le tube.</summary>
    public const string HelperExeName = "DrDxt.ElevatedHelper.exe";

    /// <summary>Le chemin attendu du helper : l'exe pose a cote du binaire courant
    /// (l'App et le service de reconciliation sont tous deux installes a cote de lui).
    /// Rend null si le fichier n'est pas la : le client refuse alors la session
    /// plutot que de faire confiance a n'importe qui.</summary>
    public static string? TryResolveExpectedHelperPath()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir))
                return null;
            var candidate = Path.Combine(baseDir, HelperExeName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Le chemin complet de l'image du processus qui sert ce tube, ou null si
    /// le pid ne se resout pas, si le processus a disparu, ou si le systeme refuse la
    /// requete. Un null vaut un refus cote appelant.</summary>
    public static string? TryGetServerImagePath(NamedPipeClientStream client)
    {
        if (client is null || !client.IsConnected)
            return null;
        try
        {
            var pipe = client.SafePipeHandle.DangerousGetHandle();
            if (!GetNamedPipeServerProcessId(pipe, out var pid) || pid == 0)
                return null;
            return TryGetProcessImagePath(unchecked((int)pid));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Le chemin complet de l'image d'un processus vivant, ou null si le pid est
    /// invalide, le processus disparu, ou la requete refusee. Public parce que c'est lui
    /// qu'on mesure : se resoudre soi-meme serait un cas trop favorable.</summary>
    public static string? TryGetProcessImagePath(int pid)
    {
        if (pid <= 0)
            return null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)pid));
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var taille = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref taille)
                ? buffer.ToString(0, (int)taille)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
