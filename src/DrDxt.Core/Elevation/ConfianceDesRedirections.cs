using System.Runtime.InteropServices;

namespace DrDxt.Core;

/// <summary>
/// Un processus élevé de DrDXT ne suit plus une jonction ni un lien symbolique créé par un compte qui n'est pas administrateur :
/// la protection RedirectionGuard de Windows (PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY, EnforceRedirectionTrust), posée
/// sur le processus entier. Sans elle, un programme du compte qui ferait de %LOCALAPPDATA%\DrDXT une jonction vers une racine
/// protégée y ferait écrire le processus élevé. Windows 10 22H2 et Windows 11 seulement : ailleurs l'appel échoue, l'échec se
/// dit, et la défense de FichierJsonAtomique (aucun point d'analyse sur le chemin d'une écriture élevée) tient seule.
/// À poser au démarrage d'un processus élevé seulement : un processus du compte doit pouvoir suivre ses propres jonctions
/// (une bibliothèque de jeux déplacée).
/// </summary>
public static class ConfianceDesRedirections
{
    public const string CodeErreur = ErrorCodes.RedirectionTrust;

    public static Result Activer()
    {
        if (!OperatingSystem.IsWindows()) return Result.Fail(CodeErreur, "Not Windows.");
        var drapeaux = AppliquerLaConfiance;
        return SetProcessMitigationPolicy(PolitiqueDeConfianceDesRedirections, ref drapeaux, sizeof(uint))
            ? Result.Ok()
            : Result.Fail(CodeErreur, $"RedirectionGuard not available: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    private const int PolitiqueDeConfianceDesRedirections = 16; // ProcessRedirectionTrustPolicy
    private const uint AppliquerLaConfiance = 0x1; // EnforceRedirectionTrust

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int politique, ref uint drapeaux, nuint taille);
}
