using System.IO;

namespace DrDxt.App.Updates;

/// <summary>
/// L'installeur vérifié reste dans le staging (%LOCALAPPDATA%\DrDXT\staging\app-staging) tant qu'il n'a pas servi : la charge
/// déjà téléchargée est reprise au clic suivant. Une fois installé, il n'a plus d'usage. Au lancement, un DrDXT-Setup-&lt;v&gt;.exe
/// d'une version déjà installée ou plus ancienne dit qu'il a servi : le dossier part. Une charge seule (téléchargée, jamais
/// lancée) ou une version plus récente reste.
/// </summary>
public static class NettoyageDuStaging
{
    public static void Purger(string dossierStaging, Version installee)
    {
        try
        {
            // Élevée, l'App ne vide jamais un dossier du compte à travers une jonction (FichierJsonAtomique.RefusEleve).
            if (!Directory.Exists(dossierStaging) || DrDxt.Core.FichierJsonAtomique.RefusEleve(dossierStaging, "STAGING") is not null) return;
            var servi = Directory.EnumerateFiles(dossierStaging, "DrDXT-Setup-*.exe")
                .Select(f => Path.GetFileNameWithoutExtension(f)["DrDXT-Setup-".Length..])
                .Any(v => Version.TryParse(v, out var lue) && lue <= installee);
            if (servi) Directory.Delete(dossierStaging, recursive: true);
        }
        catch (Exception) { /* un staging qui ne part pas reste pour le lancement suivant */ }
    }
}
