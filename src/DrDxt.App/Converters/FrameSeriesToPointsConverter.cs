using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DrDxt.App.Converters;

/// <summary>
/// Hand-built frametime A/B plotter (no charting NuGet). Turns the series to draw into a
/// scaled <see cref="PointCollection"/> for a <c>Polyline</c>. Bindings:
/// <list type="number">
///   <item>values[0] — the series to plot (IEnumerable&lt;double&gt;)</item>
///   <item>values[1] — the before series, and values[2] — the after series, both used only
///         to compute a SHARED min/max so the two polylines sit on the same vertical scale.</item>
/// </list>
/// <c>ConverterParameter</c> = "width,height" of the plot area in DIPs. Lower frame time is
/// better, so we map the max value to y=0 (top) and the min to y=height (bottom) — i.e. a
/// taller line means worse frames, matching the "lower and flatter is better" caption.
/// </summary>
public sealed class FrameSeriesToPointsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var series = ToList(values is { Length: > 0 } ? values[0] : null);
        if (series.Count == 0)
            return new PointCollection();

        var before = ToList(values is { Length: > 1 } ? values[1] : null);
        var after = ToList(values is { Length: > 2 } ? values[2] : null);

        (double w, double h) = ParseSize(parameter);

        // Shared scale across both series so before/after are visually comparable.
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in before.Concat(after).Concat(series))
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (double.IsInfinity(min) || double.IsInfinity(max))
            return new PointCollection();

        double range = max - min;
        var points = new PointCollection(series.Count);

        if (series.Count == 1)
        {
            // A single sample: draw a flat midline rather than a degenerate dot.
            points.Add(new Point(0, h / 2));
            points.Add(new Point(w, h / 2));
            return points;
        }

        double stepX = w / (series.Count - 1);
        for (int i = 0; i < series.Count; i++)
        {
            double norm = range <= double.Epsilon ? 0.5 : (series[i] - min) / range;
            double y = h - (norm * h); // higher value (worse) -> nearer the top
            points.Add(new Point(i * stepX, y));
        }
        return points;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static List<double> ToList(object? value)
    {
        var result = new List<double>();
        if (value is IEnumerable e and not string)
        {
            foreach (var item in e)
            {
                if (item is double d)
                    result.Add(d);
                else if (item is IConvertible c)
                    result.Add(c.ToDouble(CultureInfo.InvariantCulture));
            }
        }
        return result;
    }

    private static (double w, double h) ParseSize(object parameter)
    {
        if (parameter is string s)
        {
            var parts = s.Split(',');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                return (w, h);
        }
        return (520, 150);
    }
}
