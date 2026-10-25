using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// La regle du nom d'exe vivait dans l'allowlist du helper seulement ; l'appli rallumait des
/// noms que le helper refusait. Elle vit dans `NomExeDeJeu`, et son motif nulle part ailleurs dans `src/`.
/// </summary>
public sealed class UneSeuleRegleDeNomExeTests
{
    [Theory]
    [InlineData("cod.exe", true)]
    [InlineData("ModernWarfare.EXE", true)]
    [InlineData("co\0d.exe", false)]
    [InlineData("cod'.exe", false)]
    [InlineData(@"..\cod.exe", false)]
    [InlineData("cod.exe\n", false)]
    [InlineData("cod", false)]
    [InlineData(null, false)]
    [InlineData("League of Legends.exe", true)] // un jeu connu de NomExeDeJeu, dont le nom a des espaces
    [InlineData(" cod.exe", false)]
    [InlineData("cod .exe", false)]
    [InlineData("Kod.exe", false)] // signe Kelvin : IgnoreCase le rapprochait de « k »
    [InlineData("ſod.exe", false)] // s long
    public void La_regle_du_nom_d_exe(string? nom, bool valide)
        => NomExeDeJeu.EstValide(nom).Should().Be(valide);

    [Fact]
    public void Chaque_jeu_connu_a_un_nom_que_le_helper_accepte()
    {
        var connus = (IEnumerable<string>)typeof(NomExeDeJeu)
            .GetField("JeuxConnus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        connus.Should().Contain("cs2.exe", "témoin : la liste est lue");
        connus.Where(n => !NomExeDeJeu.EstValide(n)).Should().BeEmpty();
    }

    [Fact]
    public void Le_motif_n_existe_qu_une_fois_dans_src()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var lus = FichiersDuDepot.Sous(Path.Combine(dir!.FullName, "src"), "*.cs").ToList();
        lus.Should().HaveCountGreaterThan(100, "un crible qui ne lit rien ne trouve rien");

        lus.Where(f => File.ReadAllText(f).Contains(@"\.[Ee][Xx][Ee]\z"))
            .Select(Path.GetFileName)
            .Should().Equal("NomExeDeJeu.cs");
    }
}
