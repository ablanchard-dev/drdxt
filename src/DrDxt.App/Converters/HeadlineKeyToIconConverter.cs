using System.Globalization;
using System.Windows.Data;

namespace DrDxt.App.Converters;

/// <summary>
/// Maps the dashboard headline key to a stable icon key (resolved by
/// <see cref="IconKeyToGeometryConverter"/>) so the badge carries icon + text + colour
/// — never colour alone (a11y): measurable gain → check, regression-reverted → warning,
/// no measurable gain → info.
/// </summary>
/// <remarks>
/// 17/09/2026 — le seul usage de ce convertisseur (`DashboardView.xaml`, `Path.Data`)
/// recevait la CLE d'icone (« Check ») la ou WPF attend une `Geometry`. Resultat :
/// `FormatException` a chaque realisation de la vue, repli silencieux, et **aucune
/// icone dessinee**. Le badge ne portait donc que la couleur et le texte — exactement
/// ce que le commentaire ci-dessus jurait d'eviter au titre de l'accessibilite.
/// Rien ne levait : une liaison qui echoue ecrit dans une trace que personne ne lisait.
/// La cle reste rendue telle quelle si la cible est une chaine (tests, infobulle) ;
/// la geometrie est resolue quand la cible en demande une.
/// </remarks>
[ValueConversion(typeof(string), typeof(string))]
public sealed class HeadlineKeyToIconConverter : IValueConverter
{
    private static readonly IconKeyToGeometryConverter Geometries = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cle = (value as string) switch
        {
            "dashboard.measurable_gain" => "Check",
            "dashboard.regression_reverted" => "Warning",
            _ => "Info",
        };

        return typeof(System.Windows.Media.Geometry).IsAssignableFrom(targetType)
            ? Geometries.Convert(cle, targetType, parameter, culture)
            : cle;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
