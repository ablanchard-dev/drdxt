using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DrDxt.Core;

namespace DrDxt.App.Updates;

/// <summary>
/// « Installer » : lancer l'installeur signe deja verifie (Inno Setup, PrivilegesRequired=admin, qui demande l'UAC et
/// ferme l'App), pas un remplacement maison des binaires. L'empreinte est relue juste avant le lancement, binaire
/// verrouille en partage lecture seule jusqu'apres : le staging est ecrivable par l'utilisateur, et sans verrou ce qui
/// part sous UAC pourrait etre le code d'un attaquant.
/// </summary>
public static class InstallationMiseAJour
{
    /// <summary>Le fichier lancé n'est plus celui qui a été vérifié (empreinte) : la version n'est plus proposée.</summary>
    public const string CodeRefus = "UPDATE_INSTALLER_REFUSED";

    /// <summary>L'installeur n'est pas parti (introuvable, autorisation Windows refusée, disque) : rien n'a changé, la
    /// version reste proposée et un nouveau clic réessaie.</summary>
    public const string CodeNonLance = "UPDATE_INSTALLER_NOT_LAUNCHED";

    /// <summary>Refus parce que l'App tourne élevée : la version reste proposée, relancer l'App normalement suffit.</summary>
    public const string CodeEleve = "UPDATE_INSTALLER_ELEVATED";

    /// <param name="cheminStage">La charge verifiee (payload.pkg).</param>
    /// <param name="sha256Attendu">L'empreinte du manifeste signe.</param>
    /// <param name="version">Pour nommer l'installeur (ShellExecute exige un .exe).</param>
    /// <param name="lancer">Lance l'executable ; par defaut ShellExecute (UAC par l'installeur).</param>
    /// <param name="eleve">Vrai si l'App tourne elevee ; par defaut, le jeton du processus. Couture de test.</param>
    public static Result Lancer(string cheminStage, string sha256Attendu, string version, Func<string, bool>? lancer = null,
        Func<bool>? eleve = null)
    {
        try
        {
            // Depuis une App elevee, le setup heriterait du jeton administrateur et chargerait ce qui est pose a cote de lui,
            // dans un dossier du compte ; depuis l'App normale, seule la partie d'Inno qui demande l'UAC est administrateur
            // (docs/SECURITE.md §9). UAC coupé : aucune frontière à garder.
            if ((eleve ?? (() => ProcessusEleve() && !DrDxt.App.Platform.CompteDuJoueur.UacCoupe()))())
                return Result.Fail(CodeEleve, Docteur.T("MAJ.InstalleurEleve"));
            if (!File.Exists(cheminStage))
                return Result.Fail(CodeNonLance, Docteur.T("MAJ.InstalleurAbsent"));

            var exe = Path.Combine(Path.GetDirectoryName(cheminStage)!, $"DrDXT-Setup-{Nettoyer(version)}.exe");
            File.Copy(cheminStage, exe, overwrite: true);

            // Ouvert en partage lecture seule de la relecture jusqu'apres le lancement : sous %LOCALAPPDATA%, tout code du
            // compte pourrait le remplacer entre l'empreinte et Process.Start. L'execution passe, ecriture et suppression
            // sont refusees.
            var verrou = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                // La relecture porte sur le fichier qui va etre lance, pas sur sa source.
                var empreinte = Convert.ToHexString(SHA256.HashData(verrou)).ToLowerInvariant();
                if (!string.Equals(empreinte, sha256Attendu, StringComparison.OrdinalIgnoreCase))
                {
                    verrou.Dispose();
                    File.Delete(exe);
                    return Result.Fail(CodeRefus, Docteur.T("MAJ.InstalleurChange"));
                }

                return (lancer ?? LancerParLeShell)(exe)
                    ? Result.Ok()
                    : Result.Fail(CodeNonLance, Docteur.T("MAJ.InstalleurNonLance"));
            }
            finally
            {
                verrou.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Result.Fail(CodeNonLance, $"{Docteur.T("MAJ.InstalleurNonLance")} ({ex.GetType().Name})");
        }
    }

    private static bool ProcessusEleve()
    {
        using var identite = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identite).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Nettoyer(string version)
        => new(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());

    private static bool LancerParLeShell(string exe)
    {
        // Process.Start peut rendre null : rien n'a été lancé, le pied de page ne doit pas dire « installeur lancé ».
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return p is not null;
    }
}
