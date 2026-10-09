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
/// Trouve le 02/09/2026 en ecrivant le registre (D2) : « {0} problemes prouves » sans son `{0}`
/// s'affiche « problemes prouves », sans le nombre. Rien ne plante, rien ne rougit, et la seule
/// information que la ligne existe pour porter a disparu. C'est la forme la plus discrete de
/// regression de traduction : elle survit a tous les tests qui comparent des cles.
///
/// Ce gate compare le FR a l'EN, par cle : les deux doivent porter le meme jeu d'emplacements.
/// Comparer a une liste ecrite a la main derverait ; comparer les deux langues entre elles se
/// maintient tout seul.
/// </summary>
public sealed class ResxPlaceholdersSurviveTranslationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Les cles a emplacements, DERIVEES du fichier de ressources lui-meme.
    ///
    /// Elles etaient trois, ecrites a la main. Le commentaire de cette classe disait deja pourquoi
    /// c'est faux — « comparer a une liste ecrite a la main deriverait » — et la liste derivait :
    /// mesure du 05/09/2026, le produit porte 13 cles a emplacement, ce gate en surveillait 3.
    /// Les dix autres n'etaient gardees par rien, dont `Scan.Bios.Guide.EnterKey`, qui porte la
    /// TOUCHE a presser dans le BIOS : sans son emplacement, le guide dit d'appuyer sur une touche
    /// sans dire laquelle, et rien ne rougit.
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
