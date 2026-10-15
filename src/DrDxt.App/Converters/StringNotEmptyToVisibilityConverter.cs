using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Rend Collapsed quand la valeur est null ou une chaîne vide.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
