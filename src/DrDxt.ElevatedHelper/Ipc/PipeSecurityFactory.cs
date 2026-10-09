using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Restrictive named-pipe ACL factory.
//
// SECURITY (master plan §9.13):
//   * The pipe is created with PipeOptions.FirstPipeInstance so a squatter
//     cannot pre-create the name and impersonate the server. If the name is
//     already taken, creation fails loudly rather than silently sharing.
//   * The DACL grants pipe access only to: the creating user, the local
//     Administrators group, and SYSTEM. Everyone else is implicitly denied
//     (no Everyone/Authenticated-Users ACE). The ACL is *protected* so it
//     does not inherit a looser parent ACL.
// Windows-only by construction (net8.0-windows).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public const string DefaultPipeName = "DrDXT.Elevated";

    /// <summary>Builds a restrictive <see cref="PipeSecurity"/> granting full
    /// control to the current user, Administrators and SYSTEM only.</summary>
    public static PipeSecurity CreateRestrictiveSecurity()
    {
        var security = new PipeSecurity();

        // Start from an empty, protected DACL (no inherited ACEs).
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

        // 26/09/2026 : .NET ne pose pas PIPE_REJECT_REMOTE_CLIENTS (NamedPipeServerStream.Windows.cs,
        // dotnet/runtime), et PipeOptions ne l'expose pas. Une session reseau qui passe les regles
        // ci-dessus (meme compte, ou un administrateur, par SMB) atteignait donc le tube. Le refus
        // NETWORK est l'equivalent documente ; un refus passe avant les autorisations. Le local n'en
        // porte pas le SID : la connexion de l'appli reste permise (LeTubeDuHelperRefuseLeReseauTests).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        return security;
    }

    /// <summary>Creates the ACL'd, single-instance server pipe stream.</summary>
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
