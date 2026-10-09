using System.Text.RegularExpressions;

namespace DrDxt.App.ViewModels;

/// <summary>
/// UI1 (08/10/2026) : « 1 partie(s) relevée(s), 3 arrêt(s) brutal(aux) ». Un texte formaté s'accorde ici : chaque mot
/// marqué « (s) » ou « (aux) » prend le nombre qui le précède dans la même proposition (une virgule continue, un point,
/// deux-points ou point-virgule coupent). Sans nombre devant, le mot garde sa marque : rien n'est deviné.
/// </summary>
public static partial class Pluriels
{
    [GeneratedRegex(@"(?<!\{)(?<n>\d+)(?!\d*\})|(?<coupe>[;.:])|(?<mot>\p{L}+)\((?<m>s|aux)\)")]
    private static partial Regex Jeton();

    /// <param name="culture">La langue du texte. Le français met au singulier 0 et 1 ; l'anglais, 1 seul (revue n° 15 :
    /// « 0 game recorded »). Défaut : la langue de l'interface.</param>
    public static string Accorder(string texte, System.Globalization.CultureInfo? culture = null)
    {
        var francais = (culture ?? System.Globalization.CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "fr";
        int? nombre = null;
        return Jeton().Replace(texte, m =>
        {
            // Revue n° 15 : un nombre trop long (un SID dans un nom de tâche) faisait lever int.Parse ; illisible = aucun nombre.
            if (m.Groups["n"].Success)
            {
                nombre = int.TryParse(m.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lu) ? lu : null;
                return m.Value;
            }
            if (m.Groups["coupe"].Success) { nombre = null; return m.Value; }
            if (nombre is not { } n) return m.Value;
            var mot = m.Groups["mot"].Value;
            if (francais ? n < 2 : n == 1) return mot;
            return m.Groups["m"].Value == "aux" && mot.EndsWith("al", StringComparison.Ordinal) ? mot[..^2] + "aux" : mot + "s";
        });
    }
}
