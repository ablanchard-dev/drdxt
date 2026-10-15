using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Un même arrêt peut recevoir quatre constats (pilote installé avant, changement noté avant, réglage BIOS,
/// inventaire).
/// Quatre « premières » ne font aucune première : aucun texte montré au joueur ne s'attribue ce rang. Chacun est une piste.
/// </summary>
public class UneSeulePremierePisteTests
{
    [Fact]
    public void Aucun_texte_montre_ne_se_dit_la_premiere_piste()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var src = Path.Combine(d!.FullName, "src");
        var fichiers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        var fautes = fichiers
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => !x.l.TrimStart().StartsWith("//") && x.l.Contains("première piste") && x.l.Contains('"'))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        // Témoin de lecture : un dossier déplacé rendrait ce crible vert sur zéro fichier.
        fichiers.Should().HaveCountGreaterThan(300, "le crible doit avoir lu le vrai code du produit");
        fautes.Should().BeEmpty();
    }
}
