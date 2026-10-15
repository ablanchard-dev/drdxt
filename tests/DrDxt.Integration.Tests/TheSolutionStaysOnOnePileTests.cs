using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// La solution tourne sur une seule pile, et c'est un test qui le tient.
///
/// Le plan de migration prevoyait `Directory.Packages.props` (central package management) et un TFM
/// centralise dans `Directory.Build.props`. Le MOTIF etait mesure : ~36 declarations dupliquees sur
/// ~30 csproj, deja divergentes -- `System.ServiceProcess.ServiceController` existait en **8.0.0 et
/// 8.0.1** en meme temps.
///
/// Ce motif a disparu. Mesure du 07/09/2026, apres le lot .NET 10 : **zero paquet declare a deux
/// versions**, et **51 csproj sur 51** en `net10.0` ou `net10.0-windows`. Deplacer 51 fichiers pour
/// obtenir un etat qu'ils ont deja, c'est prendre un risque de refactor contre un benefice nul.
///
/// Ce qui manquait n'etait pas la centralisation : c'etait la **garantie**. Rien n'empechait le
/// prochain csproj de repartir en `net8.0-windows` ou de rappeler un paquet en 8.x, et personne ne
/// l'aurait vu avant un bug de runtime. Ce fichier est cette garantie, et il coute 30 lignes au
/// lieu de 51 fichiers.
///
/// Il ne remplace pas le CPM le jour ou il y aura vraiment quelque chose a centraliser. Il rend
/// simplement la centralisation facultative au lieu d'urgente — et il rougit si elle redevient
/// necessaire.
/// </summary>
public sealed class TheSolutionStaysOnOnePileTests
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

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    /// <summary>Tous les csproj du depot, hors artefacts de compilation.
    ///
    /// `_wpftmp.csproj` est exclu, et ce n'est pas du confort : MSBuild genere ces projets
    /// temporaires pendant la compilation WPF **puis les supprime**. Enumeres par une suite qui
    /// tourne en parallele d'un build, ils apparaissent dans la liste et ont disparu au moment de
    /// la lecture — le test echouait alors sur un `FileNotFoundException` sans aucun rapport avec
    /// ce qu'il verifie. Un test qui rougit pour une raison etrangere ne protege plus rien.</summary>
    private static IReadOnlyList<string> Csproj() =>
        FichiersDuDepot.Sous(RepoRoot(), "*.csproj")
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains("_wpftmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Le contenu d'un csproj, ou <c>null</c> s'il s'est volatilise entre l'enumeration
    /// et la lecture. Voir <see cref="Csproj"/> : c'est arrive, et ca rendait le test faussement
    /// rouge.</summary>
    private static string? Lire(string chemin)
    {
        try { return File.ReadAllText(chemin); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [Fact]
    public void Aucun_projet_ne_reste_sur_une_pile_hors_support()
    {
        // .NET 8 sort de support le 10/11/2026 (source : dotnet.microsoft.com/platform/support/policy,
        // lu le 06/09/2026). Un seul csproj oublie suffit a ramener la pile entiere.
        var fautifs = new List<string>();
        foreach (var p in Csproj())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(p), @"<TargetFrameworks?>([^<]+)</"))
            foreach (var tfm in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (!tfm.Trim().StartsWith("net10.0", StringComparison.OrdinalIgnoreCase))
                    fautifs.Add($"{Path.GetFileName(p)} → {tfm.Trim()}");
        }

        fautifs.Should().BeEmpty("tout le depot doit compiler sur la meme pile supportee");
    }

    [Fact]
    public void Le_depot_a_bien_des_projets_a_verifier()
    {
        // La garde de la garde : un filtre trop zele rendrait les deux autres tests toujours verts.
        Csproj().Should().HaveCountGreaterThan(40);
    }

    [Fact]
    public void Aucun_paquet_n_est_declare_a_DEUX_versions_differentes()
    {
        // Le defaut exact que le CPM devait fermer : `System.ServiceProcess.ServiceController`
        // vivait en 8.0.0 ET 8.0.1 en meme temps. NuGet tranche alors en silence, et ce n'est pas
        // toujours dans le sens qu'on croit.
        var versions = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Csproj())
        {
            var texte = Lire(p);
            if (texte is null) continue;

            foreach (Match m in Regex.Matches(texte,
                         "<PackageReference\\s+Include=\"(?<id>[^\"]+)\"[^>]*Version=\"(?<v>[^\"]+)\""))
            {
                var id = m.Groups["id"].Value;
                if (!versions.TryGetValue(id, out var set))
                    versions[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(m.Groups["v"].Value);
            }
        }

        versions.Should().NotBeEmpty("il doit y avoir des paquets a verifier");
        versions.Where(kv => kv.Value.Count > 1)
            .Select(kv => $"{kv.Key} → {string.Join(" / ", kv.Value)}")
            .Should().BeEmpty("un paquet a deux versions se resout en silence, et pas toujours bien");
    }
}
