using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// 26/09/2026. Le helper tourne en administrateur. Un outil lance par son nom seul (« sc.exe ») est cherche
/// par CreateProcess dans le dossier de l'appli puis dans le dossier courant, avant System32 (Microsoft Learn,
/// CreateProcessW, lpCommandLine) : un executable du meme nom pose la tournerait eleve. Tous les lancements du
/// helper passent aujourd'hui par un chemin absolu vers System32 ; l'installeur a deja sa garde
/// (DesinstallationInstalleurTests), le helper n'en avait pas.
/// </summary>
public sealed class AucunExecutableLanceParSonNomNuTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    // Un litteral passe directement comme programme : new ProcessStartInfo("x.exe" ou FileName = "x.exe".
    private static readonly Regex NomEnDur = new(
        @"(new\s+ProcessStartInfo\s*\(\s*""|(?<![A-Za-z_])FileName\s*=\s*"")", RegexOptions.CultureInvariant);

    [Fact]
    public void Le_helper_ne_lance_aucun_programme_par_un_nom_ecrit_en_dur()
    {
        var src = Path.Combine(Racine(), "src", "DrDxt.ElevatedHelper");
        var lignes = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .ToList();

        // Le crible prouve qu'il a lu : les lancements reels du helper (sc, bcdedit, dism...) sont bien vus.
        lignes.Count(x => x.l.Contains("new ProcessStartInfo(")).Should().BeGreaterThan(5);

        var fautes = lignes
            .Where(x => NomEnDur.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        fautes.Should().BeEmpty("un programme lance par le helper eleve se designe par son chemin absolu");
    }

    [Fact]
    public void Le_temoin_attrape_bien_un_nom_en_dur()
    {
        NomEnDur.IsMatch("var psi = new ProcessStartInfo(\"sc.exe\", args)").Should().BeTrue();
        NomEnDur.IsMatch("FileName = \"bcdedit.exe\",").Should().BeTrue();
        NomEnDur.IsMatch("new ProcessStartInfo(Path.Combine(dir, \"sc.exe\"))").Should().BeFalse();
        // Une constante dont le NOM finit par FileName n'est pas un lancement.
        NomEnDur.IsMatch("private const string PresentMonBinaryFileName = \"PresentMonService.exe\";").Should().BeFalse();
    }
}
