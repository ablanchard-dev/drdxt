using System.Globalization;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Formats a numeric metric for display in the user's culture (decimal separator included).
/// <c>ConverterParameter</c> is an optional .NET numeric format string (default "0",
/// integer). A null value (e.g. an unavailable latency estimate) renders as an em dash so
/// the dashboard never shows "0" where it has no reading.
/// </summary>
public sealed class NumberDisplayConverter : IValueConverter
{
    private const string Dash = "—";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Dash;

        if (value is not IConvertible convertible)
            return Dash;

        double d = convertible.ToDouble(CultureInfo.InvariantCulture);
        string format = parameter as string ?? "0";
        // 23/09/2026 : dans la culture de l'utilisateur, comme le reste de l'app (« 9,5 ns » au scan).
        // En invariant, l'écran d'après « Optimiser » écrivait « 13.1 ms ».
        return d.ToString(format, CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
