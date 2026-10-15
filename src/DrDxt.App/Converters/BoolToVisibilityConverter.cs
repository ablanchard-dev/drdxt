using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Convertit un bool en Visibility. Le paramètre « Invert » inverse la logique.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s == "Invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
