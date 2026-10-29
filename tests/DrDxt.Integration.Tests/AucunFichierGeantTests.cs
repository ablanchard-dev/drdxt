using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un fichier de plusieurs milliers de lignes ne se relit plus. Au-delà du plafond, il se coupe par thème en fichiers
/// partial, comme ScanViewModel, RepairAuditService ou CartesAgirTests, au lieu de grossir.
/// </summary>
public sealed class AucunFichierGeantTests
{
    private const int PlafondDeLignes = 1000;

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_fichier_de_code_ne_depasse_le_plafond()
    {
        var racine = Racine();
        var lus = new[] { "src", "tests", "tools", "server" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d), "*.cs"))
            .ToList();
        lus.Should().HaveCountGreaterThan(400, "src seul compte plus de 450 fichiers .cs");

        lus.Select(f => (Fichier: Path.GetRelativePath(racine, f), Lignes: File.ReadLines(f).Count()))
            .Where(f => f.Lignes > PlafondDeLignes)
            .Select(f => $"{f.Fichier} : {f.Lignes} lignes")
            .Should().BeEmpty($"au-delà de {PlafondDeLignes} lignes, un fichier se coupe par thème en fichiers partial");
    }
}
