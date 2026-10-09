using System.IO;
using System.Text.RegularExpressions;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D5.6, revue) : « Maintiens pour …, ou Entrée » six fois par écran. La consigne ne paraît
// qu'au survol ou au focus clavier de sa ligne ; le geste s'apprend sur le rond de l'accueil, où elle reste écrite.
public sealed class ConsigneAuSurvolTests
{
    [Theory]
    [InlineData(false, false, 0.0)]
    [InlineData(true, false, 1.0)]
    [InlineData(false, true, 1.0)]
    public void Visible_au_survol_ou_au_focus_clavier_seulement(bool survol, bool focus, double attendu)
        => ConsigneAuSurvol.Opacite(survol, focus).Should().Be(attendu);

    [Fact]
    public void Les_six_consignes_des_cartes_Agir_suivent_la_regle_et_l_accueil_garde_la_sienne()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DrDXT.sln"))) dir = Path.GetDirectoryName(dir)!;
        var vue = File.ReadAllText(Path.Combine(dir, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        Regex.Matches(vue, @"<TextBlock Text=""\{Binding Label\w+Maintenir\}"" effets:ConsigneAuSurvol\.Actif=""True""").Count.Should().Be(6);
        Regex.IsMatch(vue, @"LabelMaintenirPourLancer\}""[^>]*ConsigneAuSurvol").Should().BeFalse("l'accueil enseigne le geste");
    }
}
