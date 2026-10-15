using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Deux fichiers sources portaient un octet nul littéral (un separateur de cle, un
/// commentaire). Les outils de recherche de texte classent alors le fichier comme binaire et le
/// sautent sans rien dire : une phrase affichee au joueur est restee introuvable pendant une soiree.
/// Un octet nul s'ecrit par son echappement, jamais tel quel.
/// </summary>
public sealed class AucunOctetNulDansLesSourcesTests
{
    private static readonly string[] Extensions = [".cs", ".resx", ".xaml", ".csproj", ".md", ".ps1"];

    [Fact]
    public void Aucun_fichier_source_ne_contient_d_octet_nul()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull();

        var candidats = FichiersDuDepot.Sous(dir!.FullName, "*").Select(p => new FileInfo(p))
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Split(Path.DirectorySeparatorChar)
                .Any(p => p is "bin" or "obj" or ".git" or "node_modules"))
            .ToList();

        // Le temoin manquait : sans lui, un filtre trop strict ou une
        // racine mal trouvee rendait ce test vert sur zero fichier lu.
        candidats.Should().HaveCountGreaterThan(100, "le crible doit voir les sources du depot");

        var fautifs = candidats
            .Where(f => File.ReadAllBytes(f.FullName).Contains((byte)0))
            .Select(f => Path.GetRelativePath(dir.FullName, f.FullName))
            .ToList();

        fautifs.Should().BeEmpty();
    }
}
