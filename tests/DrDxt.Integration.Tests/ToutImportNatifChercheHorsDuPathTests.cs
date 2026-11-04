using FluentAssertions;

namespace DrDxt.Integration.Tests;

// L'attribut vaut pour un seul assemblage : le helper élevé et le service SYSTEM exécutent aussi le code des assemblages
// qu'ils référencent. Sans lui, une DLL absente de System32 est cherchée dans le dossier courant et le PATH.
public sealed class ToutImportNatifChercheHorsDuPathTests
{
    [Fact]
    public void Chaque_assemblage_qui_importe_du_natif_fixe_sa_recherche()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var src = Path.Combine(d!.FullName, "src");

        var projets = Directory.GetDirectories(src).Select(p => (
            Nom: Path.GetFileName(p),
            Textes: FichiersDuDepot.Sous(p, "*.cs")
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText).ToList())).ToList();

        var importeurs = projets.Where(p => p.Textes.Any(t => t.Contains("[DllImport(") || t.Contains("DllImport(\""))).ToList();
        importeurs.Should().HaveCountGreaterThan(4, "témoin : le crible voit les imports");

        importeurs.Where(p => !p.Textes.Any(t => t.Contains("assembly:") && t.Contains("DefaultDllImportSearchPaths")))
            .Select(p => p.Nom).Should().BeEmpty();
    }
}
