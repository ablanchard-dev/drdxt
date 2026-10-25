using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DrDxt.Core;

namespace DrDxt.App.Updates;

/// <summary>
/// « Installer » : lancer l'installeur signe deja verifie, pas un remplacement
/// maison des binaires. L'installeur (Inno Setup, PrivilegesRequired=admin) demande lui-meme l'UAC et
/// ferme l'App en cours.
///
/// L'empreinte est reverifiee juste avant le lancement : entre la verification au demarrage et le
/// clic, le fichier stage a pu etre remplace sur le disque. Un installeur lance sans cette relecture
/// executerait ce qui se trouve la, pas ce qui a ete verifie.
///
/// Relire ne suffit pas. Le binaire est verrouille (ouvert en
/// partage lecture seule) de l'empreinte jusqu'apres le lancement, sinon il resterait une fenetre ou il
/// pourrait etre remplace dans un dossier ecrivable par l'utilisateur, et ce qui part sous UAC serait
/// alors le code de l'attaquant.
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
            // Lance depuis une App elevee, le setup heriterait du jeton administrateur et chargerait ce qui est pose a
            // cote de lui, dans un dossier que le compte peut ecrire. Lance depuis l'App normale, il tourne sans
            // elevation et seule la partie d'Inno qui demande l'UAC devient administrateur (docs/SECURITE.md §9).
            // L'UAC coupé n'a pas de frontière à garder : tout programme du compte y tourne déjà élevé.
            if ((eleve ?? (() => ProcessusEleve() && !DrDxt.App.Platform.CompteDuJoueur.UacCoupe()))())
                return Result.Fail(CodeEleve, Docteur.T("MAJ.InstalleurEleve"));
            if (!File.Exists(cheminStage))
                return Result.Fail(CodeNonLance, Docteur.T("MAJ.InstalleurAbsent"));

            var exe = Path.Combine(Path.GetDirectoryName(cheminStage)!, $"DrDXT-Setup-{Nettoyer(version)}.exe");
            File.Copy(cheminStage, exe, overwrite: true);

            // Le fichier reste ouvert en partage lecture seule de la relecture jusqu'apres le
            // lancement. Sans ce verrou il resterait une fenetre entre l'empreinte et Process.Start :
            // le staging vit sous %LOCALAPPDATA%, donc tout code tournant sous le compte de
            // l'utilisateur peut y ecrire, et ce qui partirait sous UAC ne serait plus ce qui a
            // ete verifie. Sous ce partage, l'execution passe et l'ecriture comme la suppression
            // sont refusees par le systeme.
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
