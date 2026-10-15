using System.Globalization;
using System.Windows.Data;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;

namespace DrDxt.App.Converters;

/// <summary>
/// Traduit une clé resx (la forme qu'expose un view-model, par exemple
/// <c>Scan.Ab.MeasurableGain</c>) en sa chaîne, via le même <see cref="ResxLocalizer"/> que
/// le reste de l'app. La vue n'a ainsi aucun texte écrit en dur, et le VM reste un simple sac
/// de clés, pur et testable. Une clé null ou vide donne une chaîne vide ; une clé manquante
/// donne la clé elle-même (le contrat de ResxLocalizer).
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
