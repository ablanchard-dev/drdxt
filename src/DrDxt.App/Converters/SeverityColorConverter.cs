using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DrDxt.Core;

namespace DrDxt.App.Converters;

/// <summary>
/// Convertit une Severity en pinceau de la barre gauche d'une carte de constat.
/// </summary>
[ValueConversion(typeof(Severity), typeof(Brush))]
public sealed class SeverityColorConverter : IValueConverter
{
    // DESIGN.md : trois couleurs d'état, chacune liée à un mot (corail = à corriger, ambre = à regarder,
    // menthe = rien de signalé). Majeur et Mineur comptent tous deux comme « à corriger » (verdict, badge,
    // rapport) : même corail, sinon une pastille ambre dans un domaine au badge corail dirait deux mots.
    // L'information n'est pas un état : platine, jamais de turquoise.
    // Gelés : partagés par toutes les fenêtres et tous les fils, un pinceau qui ne l'est pas appartient au premier fil qui le crée.
    private static readonly SolidColorBrush ACorrigerBrush = Gele(Color.FromRgb(0xFF, 0x5C, 0x7A));
    private static readonly SolidColorBrush InfoBrush = Gele(Color.FromRgb(0xE6, 0xEA, 0xF0));
    // Un constat d'information suffit à mettre son domaine « À regarder » (ambre) : sa puce suit, au lieu du cyan
    // réservé aux actions. Une ligne de constat reçue entière prend l'ambre ; un réglage
    // proposé garde le cyan (c'est une proposition, pas un état).
    private static readonly SolidColorBrush ARegarderBrush = Gele(Color.FromRgb(0xFF, 0xB5, 0x47));

    private static SolidColorBrush Gele(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

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
