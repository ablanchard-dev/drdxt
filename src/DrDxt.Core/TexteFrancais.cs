using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Les phrases que le produit écrit en français formatent leurs nombres et leurs dates en français,
/// quelle que soit la langue de Windows. Sans cela, un Windows anglais affichait « 10.7 s » au milieu
/// d'une phrase française (vu en rejouant les tests en culture anglaise, comme le runner de la CI).
/// </summary>
public static class TexteFrancais
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Formate une chaîne interpolée en français : <c>F($"{x:0.0} s")</c> rend « 10,7 s ».</summary>
    public static string F(FormattableString texte) => texte.ToString(Fr);
}
