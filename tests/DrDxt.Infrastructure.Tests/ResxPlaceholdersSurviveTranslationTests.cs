using System.Collections;
using System.Resources;
using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// Une entree traduite qui PERD son emplacement de valeur fait disparaitre un chiffre en silence.
///
/// « {0} problemes prouves » sans son `{0}` s'afficherait « problemes prouves », sans le nombre. Rien ne plante, rien ne rougit, et la seule
/// information que la ligne existe pour porter a disparu. C'est la forme la plus discrete de
/// regression de traduction : elle survit a tous les tests qui comparent des cles.
///
/// Ce gate compare le FR a l'EN, par cle : les deux doivent porter le meme jeu d'emplacements.
/// Comparer a une liste ecrite a la main derverait ; comparer les deux langues entre elles se
/// maintient tout seul.
/// </summary>
public sealed class ResxPlaceholdersSurviveTranslationTests
{
    // Un emplacement formaté ({0:F1}, {0,5}) en est un aussi : ignoré, il pouvait disparaître d'une traduction sans bruit.
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    /// <summary>
    /// Les cles a emplacements, DERIVEES du fichier de ressources lui-meme.
    ///
    /// Une liste ecrite a la main deriverait (mesure : 3 cles surveillees pour 13 dans le produit). Une
    /// cle oubliee comme `Scan.Bios.Guide.EnterKey`, qui porte la TOUCHE a presser dans le BIOS, ferait
    /// dire au guide d'appuyer sur une touche sans dire laquelle, sans que rien ne rougisse.
    ///
    /// Enumerer le jeu de ressources anglais fait grandir ce gate tout seul a la prochaine cle.
    /// </summary>
    public static TheoryData<string> KeysCarryingNumbers()
    {
        var manager = new ResourceManager(
            "DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        var english = manager.GetResourceSet(new CultureInfo("en"), createIfNotExists: true, tryParents: true)
            ?? throw new InvalidOperationException("jeu de ressources anglais introuvable");

        var keys = new TheoryData<string>();
        foreach (DictionaryEntry entry in english)
        {
            if (entry.Value is string value && Placeholder.IsMatch(value))
            {
                keys.Add((string)entry.Key);
            }
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(KeysCarryingNumbers))]
    public void A_key_that_must_show_a_number_carries_its_placeholder_in_BOTH_languages(string key)
    {
        var en = new ResxLocalizer().Get(key, new CultureInfo("en"));
        var fr = new ResxLocalizer().Get(key, new CultureInfo("fr"));

        en.Should().NotBe(key, "la cle doit exister en anglais");
        fr.Should().NotBe(key, "la cle doit exister en francais");

        Placeholder.Matches(en).Should().NotBeEmpty($"{key} affiche un nombre : son gabarit anglais doit le porter");
        Placeholder.Matches(fr).Should().NotBeEmpty($"{key} affiche un nombre : sa traduction doit le porter aussi");
    }

    [Theory]
    [MemberData(nameof(KeysCarryingNumbers))]
    public void The_two_languages_expect_exactly_the_same_placeholders(string key)
    {
        // Un `{1}` present d'un cote et absent de l'autre est pire qu'un manque : selon la langue,
        // la ligne montre un nombre de plus ou de moins, et personne ne teste dans les deux.
        static IEnumerable<string> Slots(string s) =>
            Placeholder.Matches(s).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);

        var en = new ResxLocalizer().Get(key, new CultureInfo("en"));
        var fr = new ResxLocalizer().Get(key, new CultureInfo("fr"));

        Slots(fr).Should().Equal(Slots(en));
    }
}
