using System.Globalization;
using System.IO;
using System.Resources;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Sous chaque action qui touche la machine en profondeur, une ligne fixe dit si DrDXT sait la défaire : la commande
/// icacls et le guide BIOS se font à la main ; le mode Expert et la recherche de sous-tension, DrDXT les relit et les rend.
/// </summary>
public sealed class ChaqueActionRisqueeDitSiElleSeDefaitTests
{
    private static string Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        return File.ReadAllText(Path.Combine(d.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
    }

    /// <summary>Le libellé de cadre le plus proche après l'action, dans la même portion de la vue.</summary>
    private static string CadreApres(string action)
    {
        var vue = Vue();
        var i = vue.IndexOf(action, StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, $"témoin : {action} est dans la vue");
        var suite = vue.Substring(i, Math.Min(4000, vue.Length - i));
        var reversible = suite.IndexOf("LabelCadreReversible", StringComparison.Ordinal);
        var main = suite.IndexOf("LabelCadreALaMain", StringComparison.Ordinal);
        if (reversible < 0 && main < 0) return "aucun";
        if (main < 0 || (reversible >= 0 && reversible < main)) return "réversible";
        return "à la main";
    }

    [Theory]
    [InlineData("DataContext.CopierCommand", "à la main")]
    [InlineData("LabelBiosGuideHeader", "à la main")]
    [InlineData("LabelExpertAccord", "réversible")]
    [InlineData("LancerRechercheSousTensionCommand", "réversible")]
    public void L_action_porte_sa_ligne(string action, string attendu) => CadreApres(action).Should().Be(attendu);

    [Fact]
    public void Les_deux_lignes_existent_dans_les_deux_langues()
    {
        var textes = new ResourceManager("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        foreach (var langue in new[] { "en", "fr" })
            foreach (var cle in new[] { "Cadre.Reversible", "Cadre.ALaMain" })
                textes.GetResourceSet(new CultureInfo(langue), true, false)?.GetString(cle).Should().NotBeNullOrWhiteSpace($"{cle} en {langue}");
    }
}
