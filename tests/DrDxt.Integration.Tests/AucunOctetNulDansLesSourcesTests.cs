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

    // Un fichier enregistré dans la page de code de Windows affiche ses accents de travers, et la copie publique ne peut plus
    // y chercher les traces de la machine de référence : elle refuse de sortir. Chaque source est en UTF-8.
    [Fact]
    public void Chaque_fichier_source_est_en_utf8()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull();
        var strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        var fautifs = FichiersDuDepot.Sous(dir!.FullName, "*").Select(p => new FileInfo(p))
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git" or "node_modules"))
            .Where(f =>
            {
                try { strict.GetString(File.ReadAllBytes(f.FullName)); return false; }
                catch (System.Text.DecoderFallbackException) { return true; }
            })
            .Select(f => Path.GetRelativePath(dir.FullName, f.FullName))
            .ToList();

        fautifs.Should().BeEmpty();
    }
}
