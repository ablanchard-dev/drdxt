using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Le tube du helper est propre à chaque compte Windows (PipeSecurityFactory.DefaultPipeName) : un nom écrit en dur ailleurs
// ramènerait le tube unique, et deux comptes ouverts en même temps se bloqueraient de nouveau.
public sealed class LeTubeNeSEcritQuUneFoisTests
{
    [Fact]
    public void Aucun_fichier_de_src_n_ecrit_le_nom_du_tube_en_dur()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        var fichiers = FichiersDuDepot.Sous(Path.Combine(d.FullName, "src"), "*.cs").ToList();
        fichiers.Should().HaveCountGreaterThan(100, "un crible qui ne lit rien ne trouve rien");

        fichiers.Where(f => !f.EndsWith("PipeSecurityFactory.cs", StringComparison.Ordinal)
                            && File.ReadAllText(f).Contains("\"DrDXT.Elevated", StringComparison.Ordinal))
            .Should().BeEmpty();
    }
}
