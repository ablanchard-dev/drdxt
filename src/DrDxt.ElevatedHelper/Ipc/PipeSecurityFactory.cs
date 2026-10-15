using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Fabrique d'ACL restrictive pour le pipe nommé.
//
// Sécurité :
//   * Le pipe est créé avec PipeOptions.FirstPipeInstance pour qu'un squatteur ne puisse pas
//     créer le nom à l'avance et se faire passer pour le serveur. Si le nom est déjà pris, la
//     création échoue bruyamment au lieu de partager en silence.
//   * La DACL n'accorde l'accès au pipe qu'à : l'utilisateur créateur, le groupe local
//     Administrateurs et SYSTEM. Tous les autres sont refusés implicitement (aucune ACE
//     Everyone/Authenticated-Users). L'ACL est *protégée* : elle n'hérite pas d'une ACL parente
//     plus lâche.
// Windows seulement par construction (net8.0-windows).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public const string DefaultPipeName = "DrDXT.Elevated";

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

        // .NET ne pose pas PIPE_REJECT_REMOTE_CLIENTS (NamedPipeServerStream.Windows.cs,
        // dotnet/runtime), et PipeOptions ne l'expose pas. Une session reseau qui passe les regles
        // ci-dessus (meme compte, ou un administrateur, par SMB) atteindrait donc le tube. Le refus
        // NETWORK est l'equivalent documente ; un refus passe avant les autorisations. Le local n'en
        // porte pas le SID : la connexion de l'appli reste permise (LeTubeDuHelperRefuseLeReseauTests).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

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
