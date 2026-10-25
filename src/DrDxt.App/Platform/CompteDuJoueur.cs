using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DrDxt.App.Platform;

/// <summary>
/// Le compte Windows du joueur est-il standard (hors du groupe Administrateurs) ? L'élévation y passe par le mot de passe d'un
/// administrateur : le helper tourne alors sous ce dernier, et son tube refuse l'App du joueur. Le groupe se lit sur le jeton ;
/// un administrateur sous l'UAC porte un jeton filtré qui ne le voit pas, mais son type d'élévation (Limited) le dit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CompteDuJoueur
{
    /// <summary>Pur. <paramref name="typeElevation"/> : TOKEN_ELEVATION_TYPE (winnt.h), 1 sans jeton lié (compte standard, ou
    /// UAC coupé), 2 élevé, 3 administrateur filtré par l'UAC.</summary>
    public static bool Standard(int typeElevation, bool dansAdministrateurs) => typeElevation == 1 && !dansAdministrateurs;

    /// <summary>Pur. L'UAC coupé (EnableLUA à 0) : un administrateur sans jeton lié, qui tourne toujours élevé. Chaque
    /// programme du compte y a déjà le jeton administrateur ; « relancer DrDXT normalement » n'existe pas.</summary>
    public static bool UacCoupe(int typeElevation, bool dansAdministrateurs) => typeElevation == 1 && dansAdministrateurs;

    /// <summary>Le compte du processus ; illisible, il n'est pas dit standard (rien ne change).</summary>
    public static bool Standard() => Lire(Standard);

    /// <summary>Le compte du processus ; illisible, l'UAC n'est pas dit coupé (le refus d'installer élevé reste).</summary>
    public static bool UacCoupe() => Lire(UacCoupe);

    private static bool Lire(Func<int, bool, bool> juger)
    {
        try
        {
            using var identite = WindowsIdentity.GetCurrent();
            if (!GetTokenInformation(identite.Token, TokenElevationType, out var type, sizeof(int), out _)) return false;
            return juger(type, new WindowsPrincipal(identite).IsInRole(WindowsBuiltInRole.Administrator));
        }
        catch (Exception) { return false; }
    }

    private const int TokenElevationType = 18;   // TOKEN_INFORMATION_CLASS (winnt.h)

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr jeton, int classe, out int valeur, int taille, out int lue);
}
