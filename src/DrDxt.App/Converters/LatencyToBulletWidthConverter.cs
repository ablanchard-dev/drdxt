using System.Globalization;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Maps an estimated PC-latency value (ms) to a bullet/gauge fill width (DIPs), scaled
/// against a shared maximum so the before and after bullets are directly comparable.
/// Bindings: <c>values[0]</c> = this value (nullable double); <c>values[1]</c> = before,
/// <c>values[2]</c> = after (for the shared scale). <c>ConverterParameter</c> = track width
/// in DIPs. A null/zero value or empty scale collapses to 0. Latency is honest-by-label
/// ("estimated") in the View; this converter only sizes the bar.
/// </summary>
public sealed class LatencyToBulletWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: > 0 })
            return 0d;

        double? value = ToNullable(values[0]);
        if (value is null or <= 0)
            return 0d;

        double max = 0d;
        foreach (var v in values)
        {
            double d = ToNullable(v) ?? 0d;
            if (d > max) max = d;
        }
        if (max <= 0d)
            return 0d;

        double trackWidth = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            ? w
            : 240d;

        return Math.Clamp(value.Value / max, 0d, 1d) * trackWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double? ToNullable(object? value) =>
        value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : null;
}
