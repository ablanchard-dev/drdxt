using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DrDxt.Core;

namespace DrDxt.App.Converters;

/// <summary>
/// Converts a Severity value to the left-bar brush on a finding card.
/// </summary>
[ValueConversion(typeof(Severity), typeof(Brush))]
public sealed class SeverityColorConverter : IValueConverter
{
    // DESIGN.md : trois couleurs d'état, chacune liée à un mot (corail = à corriger, ambre = à regarder,
    // menthe = rien de signalé). 25/09/2026, vu sur photo : un constat Minor avait une pastille ambre dans
    // un domaine au badge corail « À corriger » ; l'ambre disait deux mots. Majeur et Mineur comptent tous
    // deux comme « à corriger » (verdict, badge, rapport) : même corail. L'information n'est pas un état :
    // platine (06/10/2026 : plus de turquoise dans DrDXT).
    private static readonly SolidColorBrush ACorrigerBrush = new(Color.FromRgb(0xFF, 0x5C, 0x7A));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0xE6, 0xEA, 0xF0));
    // 03/10/2026 (D10.2, revue n° 4) : un constat d'information suffit à mettre son domaine « À regarder » (ambre),
    // mais sa puce restait cyan, la couleur des actions. Une ligne de constat reçue entière prend l'ambre ; un réglage
    // proposé garde le cyan (c'est une proposition, pas un état).
    private static readonly SolidColorBrush ARegarderBrush = new(Color.FromRgb(0xFF, 0xB5, 0x47));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DrDxt.App.ViewModels.EntreeCarte { Gravite: > Severity.Info } => ACorrigerBrush,
        DrDxt.App.ViewModels.EntreeCarte { Propose: true } => InfoBrush,
        DrDxt.App.ViewModels.EntreeCarte => ARegarderBrush,
        Severity s when s > Severity.Info => ACorrigerBrush,
        _ => InfoBrush,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
