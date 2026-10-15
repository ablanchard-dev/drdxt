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
/// partage lecture seule) de l'empreinte jusqu'apres le lancement, sinon il restait une fenetre ou il
/// pouvait etre remplace dans un dossier ecrivable par l'utilisateur, et ce qui part sous UAC serait
/// alors le code de l'attaquant.
/// </summary>
public static class InstallationMiseAJour
{
    public const string CodeRefus = "UPDATE_INSTALLER_REFUSED";

    /// <param name="cheminStage">La charge verifiee (payload.pkg).</param>
    /// <param name="sha256Attendu">L'empreinte du manifeste signe.</param>
    /// <param name="version">Pour nommer l'installeur (ShellExecute exige un .exe).</param>
    /// <param name="lancer">Lance l'executable ; par defaut ShellExecute (UAC par l'installeur).</param>
    public static Result Lancer(string cheminStage, string sha256Attendu, string version, Func<string, bool>? lancer = null)
    {
        try
        {
            if (!File.Exists(cheminStage))
                return Result.Fail(CodeRefus, Docteur.T("MAJ.InstalleurAbsent"));

            var exe = Path.Combine(Path.GetDirectoryName(cheminStage)!, $"DrDXT-Setup-{Nettoyer(version)}.exe");
            File.Copy(cheminStage, exe, overwrite: true);

            // Le fichier reste ouvert en partage lecture seule de la relecture jusqu'apres le
            // lancement. Sans ce verrou il restait une fenetre entre l'empreinte et Process.Start :
            // le staging vit sous %LOCALAPPDATA%, donc tout code tournant sous le compte de
            // l'utilisateur pouvait y ecrire, et ce qui partait sous UAC n'etait plus ce qui avait
            // ete verifie. Mesure du 11/09/2026 : sous ce partage, l'execution passe et l'ecriture
            // comme la suppression sont refusees par le systeme.
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
                    : Result.Fail(CodeRefus, Docteur.T("MAJ.InstalleurNonLance"));
            }
            finally
            {
                verrou.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Result.Fail(CodeRefus, $"Installation impossible : {ex.Message}");
        }
    }

    private static string Nettoyer(string version)
        => new(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());

    private static bool LancerParLeShell(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return true;
    }
}
