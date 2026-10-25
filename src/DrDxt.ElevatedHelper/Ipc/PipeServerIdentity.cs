using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using static DrDxt.ElevatedHelper.Natif;

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

    /// <summary>Le serveur du tube tient-il les droits du helper ? Null : oui ; sinon la raison. L'image seule ne suffit pas :
    /// un programme non élevé du compte qui lance le vrai helper non élevé, depuis le vrai chemin, la passerait. Deux exigences
    /// de plus. (1) Le tube appartient aux Administrateurs ou à SYSTEM : un processus élevé le crée sous les Administrateurs,
    /// le service sous SYSTEM ; un squatteur non élevé le crée à son propre SID et ne peut pas s'en donner un autre
    /// (Administrateurs est « deny-only » dans son jeton filtré). Lu sur le handle déjà ouvert (READ_CONTROL vient avec la
    /// lecture). (2) Le jeton du serveur, s'il se lit, est élevé : le squatteur visé, du même compte, a toujours un jeton
    /// lisible par ce client ; un jeton illisible n'est pas une preuve, l'exigence (1) reste.</summary>
    public static string? RefusDePrivilege(NamedPipeClientStream client)
    {
        try
        {
            var pipe = client.SafePipeHandle.DangerousGetHandle();
            if (GetSecurityInfo(pipe, SeKernelObject, OwnerSecurityInformation, out var proprietaire, out _, out _, out _, out var descripteur) != 0)
                return "the pipe's owner cannot be read";
            try
            {
                var sid = new SecurityIdentifier(proprietaire);
                if (!sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                    return $"the pipe is owned by {sid.Value}, not by Administrators or SYSTEM";
            }
            finally
            {
                LocalFree(descripteur);
            }
            if (!GetNamedPipeServerProcessId(pipe, out var pid) || pid == 0)
                return "the process serving the pipe cannot be found";
            return JetonNonEleve(pid) ? "the process serving the pipe is not elevated" : null;
        }
        catch
        {
            return "the pipe server's privileges cannot be read";
        }
    }

    /// <summary>Vrai seulement si le jeton du processus se lit et dit « non élevé » (TokenElevation). Illisible : faux.</summary>
    internal static bool JetonNonEleve(uint pid)
    {
        var processus = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (processus == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(processus, TokenQuery, out var jeton)) return false;
            try
            {
                return GetTokenInformation(jeton, TokenElevation, out var eleve, sizeof(int), out _) && eleve == 0;
            }
            finally
            {
                CloseHandle(jeton);
            }
        }
        finally
        {
            CloseHandle(processus);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;          // TOKEN_INFORMATION_CLASS (winnt.h)
    private const int SeKernelObject = 6;           // SE_OBJECT_TYPE (accctrl.h)
    private const uint OwnerSecurityInformation = 1;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

}
