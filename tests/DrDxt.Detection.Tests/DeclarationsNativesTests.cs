using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// Une fonction native partagée n'a qu'une déclaration par projet (son Natif.cs), aux noms du SDK : une signature corrigée
// l'est partout. Et une fonction HID qui rend un BOOLEAN le lit sur un octet.
public sealed class DeclarationsNativesTests
{
    private static readonly Regex Declaration = new(@"static extern [^(]*?\b(\w+)\(", RegexOptions.Compiled);

    private static string Src()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, "src");
    }

    private static IEnumerable<string> Fichiers(string projet) => Directory.EnumerateFiles(Path.Combine(Src(), projet), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // Chaque projet et ce que sa liste partagée doit au moins porter (témoin : la liste a bien été lue).
    public static TheoryData<string, string[]> Projets => new()
    {
        { "DrDxt.Detection", ["CM_Locate_DevNodeW", "CreateFileW", "HidP_GetCaps", "CloseHandle", "StartTrace", "GetSystemCpuSetInformation"] },
        { "DrDxt.App", ["CloseHandle"] },
        { "DrDxt.ElevatedHelper", ["CloseHandle"] },
    };

    [Theory]
    [MemberData(nameof(Projets))]
    public void Chaque_fonction_partagee_n_est_declaree_qu_une_fois_dans_son_projet(string projet, string[] temoins)
    {
        var partagees = Declaration.Matches(File.ReadAllText(Path.Combine(Src(), projet, "Natif.cs"))).Select(m => m.Groups[1].Value).ToHashSet();
        partagees.Should().Contain(temoins);

        var ailleurs = Fichiers(projet).Where(f => Path.GetFileName(f) != "Natif.cs")
            .SelectMany(f => Declaration.Matches(File.ReadAllText(f)).Select(m => (Fichier: Path.GetFileName(f), Nom: m.Groups[1].Value)))
            .Where(x => partagees.Contains(x.Nom)).Select(x => $"{x.Fichier} : {x.Nom}").ToList();
        ailleurs.Should().BeEmpty();
    }

    // hidsdi.h : HidD_GetAttributes, HidD_GetProductString, HidD_GetPreparsedData et HidD_FreePreparsedData rendent un
    // BOOLEAN, un octet. Lu comme un BOOL de quatre octets, le résultat porte trois octets laissés au hasard.
    [Fact]
    public void Une_fonction_HID_qui_rend_un_BOOLEAN_le_lit_sur_un_octet()
    {
        var declarations = Fichiers("DrDxt.Detection").SelectMany(f =>
        {
            var s = File.ReadAllText(f);
            return Regex.Matches(s, @"extern bool (HidD_\w+)\(").Select(m => (Nom: m.Groups[1].Value, Avant: s[Math.Max(0, m.Index - 160)..m.Index]));
        }).ToList();
        declarations.Should().HaveCountGreaterThanOrEqualTo(4, "témoin : les quatre fonctions HidD_ des lecteurs d'entrée sont vues");
        declarations.Where(d => !d.Avant.Contains("UnmanagedType.U1", StringComparison.Ordinal)).Select(d => d.Nom).Should().BeEmpty();
    }
}
