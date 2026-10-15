using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// L'adresse e-mail du développeur, son nom d'utilisateur Windows et le nom de sa
/// machine servaient d'exemples dans des tests. Le dépôt est destiné à être public. Ce garde lit, au moment
/// où il tourne, le nom d'utilisateur et le nom de la machine qui l'exécutent, et refuse de les trouver dans
/// les fichiers du dépôt : il ne contient donc lui-même aucune de ces valeurs.
/// </summary>
public sealed class LeDepotNePortePasLaMachineDuDeveloppeurTests
{
    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    internal static IReadOnlyList<string> Motifs(string utilisateur, string machine) =>
    [
        // La forme chemin seulement : un nom d'utilisateur peut aussi être un mot courant.
        $@"Users\{utilisateur}\",
        $"Users/{utilisateur}/",
        machine,
    ];

    [Fact]
    public void Aucun_fichier_source_ne_porte_le_nom_d_utilisateur_ou_de_machine_qui_execute_le_test()
    {
        // Témoin : les motifs attrapent bien un chemin d'exemple écrit comme autrefois.
        Motifs("alicejoueuse", "PC-DE-TEST").Should().Contain(m => @"crash at C:\Users\alicejoueuse\app".Contains(m));

        var motifs = Motifs(Environment.UserName, Environment.MachineName);
        var racine = Racine();
        var sep = Path.DirectorySeparatorChar;
        var fautes = new[] { "src", "tests", "tools", "installer" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d)))
            .Where(f => !f.Contains($"{sep}Output{sep}"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".resx" or ".json" or ".ps1" or ".iss" or ".txt" or ".csproj")
            .Where(f => motifs.Any(m => File.ReadAllText(f).Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(racine, f))
            .ToList();

        fautes.Should().BeEmpty("le dépôt sera public : aucun fichier ne doit nommer la machine du développeur");
    }
}
