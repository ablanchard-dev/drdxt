using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Une application installee dans Program Files N'A pas le droit d'y ecrire : le dossier est
/// en lecture seule pour un utilisateur normal. Un chemin d'ecriture calcule a cote de
/// l'executable marche parfaitement sur la machine du developpeur (qui lance depuis bin/) et
/// casse chez le premier testeur installe -- souvent en silence, parce que l'exception tombe
/// dans un chemin best-effort.
///
/// C'est exactement le genre de defaut qu'un smoke install revele TROP TARD. Ce test le prend
/// avant, cote source : tout etat persistant (logs, config, marqueurs) doit se calculer depuis
/// un dossier utilisateur, jamais depuis le repertoire de l'application.
///
/// Verifie le 27/08/2026 : les cinq chemins d'ecriture connus passent tous par
/// LocalApplicationData -- mais rien ne l'imposait. Ce fichier est ce qui l'impose.
/// </summary>
public sealed class WritesOutsideProgramFilesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate DrDXT.sln by walking up from " + AppContext.BaseDirectory);
    }

    /// <summary>Les fichiers de PRODUCTION qui decident ou l'etat persistant est ecrit.</summary>
    private static IEnumerable<string> ProductionCsFiles()
        => FichiersDuDepot.Sous(Path.Combine(RepoRoot(), "src"), "*.cs")
            .Where(p =>
            {
                var n = p.Replace('\\', '/');
                return !n.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && !n.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string Rel(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

    /// <summary>Les racines utilisateur acceptables pour de l'etat persistant.</summary>
    private static readonly string[] AllowedRoots =
    {
        "SpecialFolder.LocalApplicationData",
        "SpecialFolder.ApplicationData",
        "SpecialFolder.CommonApplicationData", // %ProgramData% : admin-write, PAS Program Files
        "GetTempPath",
        // 27/09/2026 : le dossier des donnees en un seul endroit, resolu sous LocalApplicationData
        // (DossierDonneesTests le prouve ; UnSeulDossierDeDonneesTests interdit qu'on le recalcule ailleurs).
        "DossierDonnees.",
    };

    [Fact]
    public void PersistentStateIsWrittenUnderAUserFolder_NeverNextToTheExecutable()
    {
        // Un chemin d'ecriture bati sur le repertoire de l'application : sur une installation
        // reelle, c'est Program Files -- donc lecture seule.
        var offenders = new List<string>();

        foreach (var file in ProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i];
                var trimmed = code.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("///", StringComparison.Ordinal))
                    continue;

                // Path.Combine(AppContext.BaseDirectory, ...) = un chemin relatif a l'exe.
                // Lire a cote de l'exe est legitime (binaires embarques, DLL vendorisees) ;
                // ce qui ne l'est pas, c'est d'y ecrire un fichier d'etat. On flague donc la
                // combinaison BaseDirectory + un nom d'etat persistant sur la meme ligne.
                if (!code.Contains("AppContext.BaseDirectory", StringComparison.Ordinal))
                    continue;

                var mentionsState =
                    code.Contains(".log", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains(".json", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("settings", StringComparison.OrdinalIgnoreCase);

                if (mentionsState)
                    offenders.Add($"{Rel(file)}:{i + 1}  {code.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "un fichier d'etat calcule depuis le repertoire de l'application atterrit dans " +
            "Program Files sur une VRAIE installation, ou l'utilisateur n'a pas le droit d'ecrire :" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheKnownStateWriters_AllResolveFromAUserFolder()
    {
        // Les ecrivains connus (le marqueur de siege de licence est parti le 23/09/2026). Si l'un d'eux change de racine, ce test le dit --
        // et si un sixieme apparait ailleurs, le test precedent l'attrape.
        string[] writers =
        {
            "src/DrDxt.Infrastructure/Logging/AppLogging.cs",
            "src/DrDxt.Infrastructure/ServiceCollectionExtensions.cs",
            "src/DrDxt.Bios/PendingBiosChangeStore.cs",
        };

        foreach (var rel in writers)
        {
            var full = Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(full).Should().BeTrue($"{rel} doit exister");

            var source = File.ReadAllText(full);
            AllowedRoots.Should().Contain(root => source.Contains(root, StringComparison.Ordinal),
                $"{rel} ecrit de l'etat persistant : il doit le resoudre depuis un dossier " +
                "utilisateur (LocalApplicationData / ApplicationData / CommonApplicationData), " +
                "jamais depuis le repertoire d'installation");
        }
    }
}
