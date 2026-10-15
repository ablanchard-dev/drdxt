using System.Globalization;
using System.Windows.Media;
using DrDxt.App.Converters;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// DESIGN.md : une couleur d'état = un mot. Majeur et Mineur se lisent « à corriger »
// partout (verdict, badge, rapport) : une pastille ambre sur un Mineur dirait « à regarder ».
public class SeverityColorConverterTests
{
    private static Color Teinte(Severity s) =>
        ((SolidColorBrush)new SeverityColorConverter().Convert(s, typeof(Brush), null!, CultureInfo.InvariantCulture)).Color;

    [Fact]
    public void Majeur_et_mineur_ont_la_couleur_du_mot_a_corriger()
    {
        var corail = Color.FromRgb(0xFF, 0x5C, 0x7A);
        Teinte(Severity.Major).Should().Be(corail);
        Teinte(Severity.Minor).Should().Be(corail, "l'ambre est reservee a « à regarder »");
    }

    [Fact]
    public void L_information_n_emprunte_aucune_couleur_d_etat()
        // L'accent de DrDXT est le platine.
        => Teinte(Severity.Info).Should().Be(Color.FromRgb(0xE6, 0xEA, 0xF0));

    // Un constat d'information met son domaine « À regarder » ; sa puce prend
    // l'ambre de ce mot. Un réglage proposé reste cyan. Les gravités à corriger restent corail.
    private static Color Teinte(DrDxt.App.ViewModels.EntreeCarte e) =>
        ((SolidColorBrush)new SeverityColorConverter().Convert(e, typeof(Brush), null!, CultureInfo.InvariantCulture)).Color;

    private static DrDxt.App.ViewModels.EntreeCarte Ligne(Severity g, bool propose = false) =>
        new("memoire", g, "t", "d", string.Empty, string.Empty, false, propose);

    [Fact]
    public void Une_ligne_de_constat_prend_la_couleur_du_mot_de_son_domaine()
    {
        Teinte(Ligne(Severity.Info)).Should().Be(Color.FromRgb(0xFF, 0xB5, 0x47), "constat d'information = « à regarder »");
        Teinte(Ligne(Severity.Minor)).Should().Be(Color.FromRgb(0xFF, 0x5C, 0x7A));
        Teinte(Ligne(Severity.Info, propose: true)).Should().Be(Color.FromRgb(0xE6, 0xEA, 0xF0), "une proposition n'est pas un état");
    }
}
