using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Converts an item count to <see cref="Visibility"/>: a positive count → Visible,
/// zero → Collapsed. Pass <c>ConverterParameter="invert"</c> to flip (show only when the
/// list is empty — e.g. the "no changes in this category" placeholder).
/// </summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is IConvertible c ? c.ToInt32(CultureInfo.InvariantCulture) : 0;
        bool hasItems = count > 0;
        bool invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        if (invert) hasItems = !hasItems;
        return hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
