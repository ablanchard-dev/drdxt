using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DrDxt.App.ViewModels;

namespace DrDxt.App.Converters;

/// <summary>
/// Rend Visible quand le ScanState correspond à l'un des noms d'état de ConverterParameter (séparés par
/// des virgules), sinon Collapsed. Un « ! » devant la liste inverse (afficher quand rien ne correspond).
/// </summary>
[ValueConversion(typeof(ScanState), typeof(Visibility))]
public sealed class ScanStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ScanState state || parameter is not string param)
            return Visibility.Collapsed;

        bool invert = param.StartsWith('!');
        string list = invert ? param[1..] : param;

        bool match = list.Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Any(s => Enum.TryParse<ScanState>(s.Trim(), out var parsed) && parsed == state);

        return (match ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
