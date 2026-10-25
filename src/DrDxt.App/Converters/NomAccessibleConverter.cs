using System.Globalization;
using System.Windows.Data;
using DrDxt.Core;

namespace DrDxt.App.Converters;

/// <summary>
/// Le nom qu'un lecteur d'écran annonce pour un bouton répété d'une ligne à l'autre (« Pourquoi ? », « Annuler », « Copier la
/// commande ») : l'action, puis le titre de sa ligne. Sinon dix boutons s'annoncent pareil. Le séparateur suit la langue.
/// </summary>
public sealed class NomAccessibleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => string.Join(Docteur.DeuxPoints, values.OfType<string>().Where(v => v.Length > 0));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
