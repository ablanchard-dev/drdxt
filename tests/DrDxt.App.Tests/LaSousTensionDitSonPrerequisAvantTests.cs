using System.Globalization;
using System.IO;
using System.Resources;
using DrDxt.App.ViewModels;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Sur un Intel de 12e à 14e génération, le BIOS refuse la sous-tension tant que « UnderVolt Protection » est active.
/// La carte « Trouver ma sous-tension » le dit avant le lancement, pas seulement après un refus, et sans rien promettre :
/// une carte sans cette option ne permet pas la sous-tension.
/// </summary>
public sealed class LaSousTensionDitSonPrerequisAvantTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-14700F", true)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", true)]
    // Les portables de 12e et 13e génération ont un numéro à quatre chiffres.
    [InlineData("12th Gen Intel(R) Core(TM) i7-1260P", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-1335U", true)]
    [InlineData("Intel(R) Core(TM) i7-11700K", false)]
    [InlineData("11th Gen Intel(R) Core(TM) i5-1135G7", false)]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", false)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData("", false)]
    public void Le_prerequis_vise_les_intel_de_12e_a_14e_generation(string modele, bool attendu) =>
        ScanViewModel.PrerequisUndervoltProtection(modele).Should().Be(attendu);

    [Fact]
    public void Le_texte_dit_l_option_le_menu_et_le_cas_sans_option_dans_les_deux_langues()
    {
        var textes = new ResourceManager("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        foreach (var langue in new[] { "en", "fr" })
        {
            var t = textes.GetResourceSet(new CultureInfo(langue), true, false)?.GetString("SousTension.Prerequis");
            t.Should().NotBeNullOrWhiteSpace(langue).And.Contain("UnderVolt Protection").And.Contain("OC").And.Contain("5.23");
            t!.ToLowerInvariant().Should().NotContain("microcode", "aucune promesse sur le microcode");
        }
    }

    [Fact]
    public void La_carte_montre_le_prerequis_avant_le_bouton_de_lancement()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        var vue = File.ReadAllText(Path.Combine(d.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var prerequis = vue.IndexOf("LabelSousTensionPrerequis", StringComparison.Ordinal);
        prerequis.Should().BeGreaterThan(0, "la carte porte la ligne de prérequis");
        vue.IndexOf("SousTensionPrerequisVisible", StringComparison.Ordinal).Should().BeGreaterThan(0, "et ne la montre qu'aux processeurs concernés");
        prerequis.Should().BeLessThan(vue.IndexOf("LabelSousTensionMaintenir", StringComparison.Ordinal), "elle se lit avant le lancement");
        var possible = vue.IndexOf("DataContext.SousTensionPossible", StringComparison.Ordinal);
        possible.Should().BeGreaterThan(0, "la carte ne se montre que là où la sous-tension peut se poser");
        possible.Should().BeLessThan(vue.IndexOf("LancerRechercheSousTensionCommand", StringComparison.Ordinal), "elle porte le bouton");
    }
}
