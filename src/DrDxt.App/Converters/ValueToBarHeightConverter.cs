using System.Globalization;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Scales one numeric value to a bar height (DIPs) against a shared maximum, for the
/// hand-built grouped lows bars (no charting NuGet). Bindings: <c>values[0]</c> is the
/// value to size; <c>values[1..]</c> are all the values in the group so every bar shares
/// the same scale. <c>ConverterParameter</c> = the max bar height in DIPs. A non-positive
/// or empty group yields 0 so the bar simply collapses (never throws, never negative).
/// </summary>
public sealed class ValueToBarHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: > 0 })
            return 0d;

        double value = ToDouble(values[0]);

        double max = 0d;
        foreach (var v in values)
        {
            double d = ToDouble(v);
            if (d > max) max = d;
        }
        if (max <= 0d)
            return 0d;

        double maxHeight = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            ? h
            : 120d;

        return Math.Max(0d, value / max * maxHeight);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object? value) =>
        value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0d;
}
