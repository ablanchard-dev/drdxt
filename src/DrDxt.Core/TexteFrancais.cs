using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Les phrases du produit formatent leurs nombres et leurs dates dans la langue du docteur (<see cref="Docteur.Langue"/>),
/// jamais dans celle de Windows : une phrase française garde « 10,7 s » sur un Windows anglais, et une phrase anglaise
/// écrit « 10.7 s ». Le français reste la langue par défaut du docteur.
/// </summary>
public static class TexteFrancais
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Formate une chaîne interpolée dans la langue du docteur : <c>F($"{x:0.0} s")</c> rend « 10,7 s » en
    /// français, « 10.7 s » en anglais.</summary>
    public static string F(FormattableString texte) => texte.ToString(Docteur.Langue);
}
