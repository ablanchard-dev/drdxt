using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 26/09/2026 : trois copies de la table de Student affirmaient chacune qu'il n'y en avait qu'une, et
/// divergeaient. La table vit dans `DrDxt.Core.Student` ; sa premiere valeur ne doit apparaitre nulle
/// part ailleurs dans `src/`.
/// </summary>
public sealed class UneSeuleTableDeStudentTests
{
    [Fact]
    public void La_table_de_Student_n_existe_qu_une_fois()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var src = Path.Combine(dir!.FullName, "src");

        var lus = FichiersDuDepot.Sous(src, "*.cs").ToList();
        lus.Should().HaveCountGreaterThan(100, "un crible qui ne lit rien ne trouve rien");

        lus.Where(f => File.ReadAllText(f).Contains("12.706"))
            .Select(Path.GetFileName)
            .Should().Equal("Student.cs");
    }
}
