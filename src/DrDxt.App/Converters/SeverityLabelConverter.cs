using System.Globalization;
using System.Windows.Data;
using DrDxt.Core;

namespace DrDxt.App.Converters;

/// <summary>
/// Le mot de la pastille de gravité, pour le lecteur d'écran et l'info-bulle : la couleur seule ne porte pas l'état. Même
/// correspondance que <see cref="SeverityColorConverter"/> : corail = à corriger, ambre = à regarder, platine = proposé ou
/// information.
/// </summary>
[ValueConversion(typeof(object), typeof(string))]
public sealed class SeverityLabelConverter : IValueConverter
{
    /// <param name="parameter">« Titre » : le mot en tête d'un groupe, avec sa majuscule (« À corriger »).</param>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var mot = Docteur.T(Cle(value));
        return parameter is "Titre" && mot.Length > 0 ? char.ToUpper(mot[0], Docteur.Langue) + mot[1..] : mot;
    }

    /// <summary>La clé du mot, partagée avec le regroupement des constats par gravité (<see cref="DrDxt.App.ViewModels.GroupeDeConstats"/>).</summary>
    public static string Cle(object? value) => value switch
    {
        DrDxt.App.ViewModels.EntreeCarte { Gravite: > Severity.Info } => "CG.ACorriger",
        DrDxt.App.ViewModels.EntreeCarte { Propose: true } => "CG.Propose",
        DrDxt.App.ViewModels.EntreeCarte => "CG.ARegarder",
        Severity s when s > Severity.Info => "CG.ACorriger",
        _ => "CG.Info",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
