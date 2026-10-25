using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// La détection lit. Un handle ouvert avec un droit d'écriture (GENERIC_WRITE, GENERIC_ALL) se fait refuser sur un compte
// standard, et la lecture devient « non lue » pour une question de droit, pas de matériel : la fréquence d'une souris USB se
// lit par un IOCTL FILE_ANY_ACCESS, sans aucun droit. Natif.cs le promet ; ce crible le garde.
public sealed class AucunHandleDeDetectionNEcritTests
{
    private static readonly Regex Appel = new(@"CreateFileW\(\s*[^,]+,\s*([^,]+),");
    private static readonly Regex DroitDEcriture = new(@"0x40000000|0xC0000000|0x10000000|GENERIC_WRITE|GENERIC_ALL", RegexOptions.IgnoreCase);

    [Fact]
    public void Aucun_CreateFileW_de_la_detection_ne_demande_d_ecrire()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var appels = FichiersDeDetection(d!.FullName)
            .SelectMany(f => Appel.Matches(File.ReadAllText(f)).Select(m => (Fichier: Path.GetFileName(f), Acces: m.Groups[1].Value.Trim())))
            .ToList();

        appels.Should().HaveCountGreaterThan(2, "témoin : le crible voit les ouvertures de la détection");
        appels.Where(a => DroitDEcriture.IsMatch(a.Acces)).Select(a => a.Fichier).Should().BeEmpty();
    }

    private static IEnumerable<string> FichiersDeDetection(string racine)
        => Directory.EnumerateFiles(Path.Combine(racine, "src", "DrDxt.Detection"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
