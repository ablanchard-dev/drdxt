using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le secours du BIOS (Clear CMOS, flashback) est obligatoire avant ET après les étapes : on le lit avant de toucher à quoi
// que ce soit, et on le retrouve quand une étape a mal tourné. RecoveryTop s'affichait après les étapes, et RecoveryBottom
// n'était lié nulle part.
public class GuideBiosSecoursAvantEtApresTests
{
    private static string Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
    }

    [Fact]
    public void Le_secours_s_affiche_avant_et_apres_les_etapes()
    {
        var vue = Vue();
        var avant = vue.IndexOf("{Binding BiosGuide.RecoveryTop}", StringComparison.Ordinal);
        var etapes = vue.IndexOf("{Binding BiosGuide.Steps}", StringComparison.Ordinal);
        var apres = vue.IndexOf("{Binding BiosGuide.RecoveryBottom}", StringComparison.Ordinal);

        etapes.Should().BePositive("témoin : la vue lue porte les étapes du guide");
        avant.Should().BePositive().And.BeLessThan(etapes);
        apres.Should().BeGreaterThan(etapes);
    }
}
