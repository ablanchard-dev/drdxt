using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DrDxt.App.Converters;

/// <summary>
/// Maps the dashboard headline key to its honest accent brush (a11y: the badge always
/// also carries an icon + text, this is colour only as reinforcement):
/// measurable gain → green, regression-reverted → red/amber, no measurable gain → muted.
/// Colours are pulled from the shared App.xaml tokens so the dashboard matches ScanView.
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class HeadlineKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string token = (value as string) switch
        {
            "dashboard.measurable_gain" => "GreenBrush",
            "dashboard.regression_reverted" => "BandPoorBrush",
            _ => "TextSecondaryBrush",
        };
        return System.Windows.Application.Current?.TryFindResource(token) as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
