using System.Globalization;
using System.Windows.Data;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;

namespace DrDxt.App.Converters;

/// <summary>
/// Resolves a resx KEY (the form the DashboardViewModel exposes — e.g.
/// <c>dashboard.measurable_gain</c>) to its localized string via the same
/// <see cref="ResxLocalizer"/> the rest of the app uses. This keeps the View free of
/// hardcoded copy while the VM stays a pure, testable key bag. A null/empty key yields
/// an empty string; a missing key yields the key itself (ResxLocalizer's contract).
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class KeyToLocalizedStringConverter : IValueConverter
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        return string.IsNullOrEmpty(key) ? string.Empty : Localizer.Get(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
