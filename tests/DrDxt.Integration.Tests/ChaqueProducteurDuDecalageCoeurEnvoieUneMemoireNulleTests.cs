using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Le helper rend le seul décalage cœur d'une ApplyNvApiClockOffsetOp ; le champ mémoire décrit la carte, il n'est jamais
/// écrit, et un écart se journalise sans échec (sinon un bail se rejouerait sans fin). Le faux succès qui en reste se ferme
/// ici : chaque producteur du code envoie une mémoire nulle. Un producteur qui en enverrait une autre fait tomber ce test.
/// </summary>
public sealed class ChaqueProducteurDuDecalageCoeurEnvoieUneMemoireNulleTests
{
    private static readonly Regex Construction = new(@"new ApplyNvApiClockOffsetOp\((?<args>[^;]*?)\)\s*[;,)]", RegexOptions.Singleline);
    private static readonly Regex MemoireNulle = new(@"(,\s*0|MemoryOffsetMhz:\s*0)\s*$");

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Chaque_producteur_envoie_une_memoire_nulle()
    {
        var src = Path.Combine(Racine(), "src");
        var constructions = FichiersDuDepot.Sous(src, "*.cs")
            .SelectMany(f => Construction.Matches(File.ReadAllText(f)).Select(m => (Fichier: Path.GetFileName(f), Args: m.Groups["args"].Value.Trim())))
            .ToList();

        constructions.Should().HaveCountGreaterThan(2, "témoin : les producteurs connus (sous-tension ×2, désinstallation) sont trouvés");
        constructions.Where(c => !MemoireNulle.IsMatch(c.Args)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Target.GpuIndex, core.Value, MemoryOffsetMhz: 0", true)]
    [InlineData("i, 0, 0", true)]
    [InlineData("i, 0, 300", false)]
    [InlineData("i, 0, MemoryOffsetMhz: memoire", false)]
    public void Le_crible_reconnait_une_memoire_nulle(string args, bool nulle) => MemoireNulle.IsMatch(args).Should().Be(nulle);
}
