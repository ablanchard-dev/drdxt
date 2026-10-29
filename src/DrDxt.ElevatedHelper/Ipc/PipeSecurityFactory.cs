using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Fabrique d'ACL restrictive pour le pipe nommé.
//
// Sécurité :
//   * PipeOptions.FirstPipeInstance : un squatteur ne crée pas le nom d'avance pour se faire passer pour le serveur ;
//     nom déjà pris → échec bruyant, jamais un partage silencieux.
//   * DACL protégée (sans héritage) : l'utilisateur créateur, Administrateurs et SYSTEM seulement, aucune ACE
//     Everyone/Authenticated-Users.
// Windows seulement par construction.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public const string PrefixeDuTube = "DrDXT.Elevated";

    /// <summary>Le tube du compte courant : un nom par SID, pour qu'un second compte ouvert (changement rapide
    /// d'utilisateur) crée le sien (FirstPipeInstance). L'App et son helper élevé portent le même SID.</summary>
    public static string DefaultPipeName => NomDuCompte.Value;

    // Le SID d'un processus ne change pas : lu une fois, le jeton rendu aussitôt.
    private static readonly Lazy<string> NomDuCompte = new(() =>
    {
        using var moi = WindowsIdentity.GetCurrent();
        return NomPourLeCompte(moi.User);
    });

    /// <summary>Pur : le tube d'un compte ; sans SID lisible, le préfixe seul.</summary>
    public static string NomPourLeCompte(SecurityIdentifier? sid) => sid is null ? PrefixeDuTube : $"{PrefixeDuTube}.{sid.Value}";

    /// <summary>Construit un <see cref="PipeSecurity"/> restrictif qui n'accorde le contrôle
    /// total qu'à l'utilisateur courant, aux Administrateurs et à SYSTEM.</summary>
    public static PipeSecurity CreateRestrictiveSecurity()
    {
        var security = new PipeSecurity();

        // Partir d'une DACL vide et protégée (aucune ACE héritée).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current user SID.");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        security.AddAccessRule(new PipeAccessRule(
            currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            system, PipeAccessRights.FullControl, AccessControlType.Allow));

        // .NET ne pose pas PIPE_REJECT_REMOTE_CLIENTS (source : NamedPipeServerStream.Windows.cs, dotnet/runtime) : une
        // session reseau (meme compte ou administrateur, par SMB) atteindrait le tube. Le refus NETWORK, equivalent
        // documente, passe avant les autorisations ; le local n'en porte pas le SID (LeTubeDuHelperRefuseLeReseauTests).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        // Par defaut, un objet cree par un administrateur eleve appartient a son compte, le meme qu'un squatteur non eleve
        // (la strategie « proprietaire par defaut des objets crees par les Administrateurs » peut changer ca ; le SetOwner
        // explicite vaut dans les deux cas). Le helper ou le service se donne le tube aux Administrateurs, ce qu'un jeton
        // filtre (Administrateurs « deny-only ») ne peut pas : ce qu'exige le client (PipeServerIdentity.RefusDePrivilege).
        // Un hote non eleve (tests) garde son compte.
        using (var moi = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(moi).IsInRole(WindowsBuiltInRole.Administrator))
                security.SetOwner(admins);
        }

        return security;
    }

    /// <summary>Crée le flux de pipe serveur à instance unique, protégé par ACL.</summary>
    public static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var security = CreateRestrictiveSecurity();
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
