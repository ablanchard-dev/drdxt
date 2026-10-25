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
// Windows seulement par construction.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public const string PrefixeDuTube = "DrDXT.Elevated";

    /// <summary>Le tube du compte courant. Un seul nom pour toute la machine bloquait un second compte ouvert en même
    /// temps (changement rapide d'utilisateur) : son helper ne créait pas le tube déjà pris (FirstPipeInstance). L'App et
    /// son helper élevé portent le même SID de compte, et tombent donc sur le même nom.</summary>
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

        // .NET ne pose pas PIPE_REJECT_REMOTE_CLIENTS (NamedPipeServerStream.Windows.cs,
        // dotnet/runtime), et PipeOptions ne l'expose pas. Une session reseau qui passe les regles
        // ci-dessus (meme compte, ou un administrateur, par SMB) atteindrait donc le tube. Le refus
        // NETWORK est l'equivalent documente ; un refus passe avant les autorisations. Le local n'en
        // porte pas le SID : la connexion de l'appli reste permise (LeTubeDuHelperRefuseLeReseauTests).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        // Le proprietaire par defaut d'un objet cree par un administrateur eleve est son propre compte (« createur de
        // l'objet », le reglage par defaut de Windows : un tube et un fichier crees ainsi appartiennent au compte) : le meme
        // qu'un squatteur non eleve. La strategie « proprietaire par defaut des objets crees par les Administrateurs » peut le mettre aux
        // Administrateurs ; le SetOwner explicite vaut dans les deux cas. Le helper eleve, ou le service, se
        // donne le tube aux Administrateurs, ce qu'un jeton filtre ne peut pas faire (Administrateurs y est « deny-only ») :
        // c'est ce que le client exige (PipeServerIdentity.RefusDePrivilege). Un hote non eleve (tests) garde son compte.
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
