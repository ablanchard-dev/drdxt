using System.Globalization;
using System.Resources;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 14/09/2026 (plan 2.4) — l'ecran de resultat nomme chaque levier par son titre traduit. Un levier ajoute
// au catalogue sans titre s'afficherait par son id interne : ce test le refuse, en anglais et en francais.
// Lu sans remontee aux parents (piege note dans DashboardLocalizationTests, 21/08/2026) : la premiere
// version passait par ResxLocalizer.Get, qui rend l'anglais quand le francais manque — sa moitie FR ne
// prouvait rien.
public sealed class EveryCandidateHasATitleTests
{
    private static readonly ResourceManager Resources =
        new("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private static bool ExistsIn(string culture, string key) =>
        !string.IsNullOrEmpty(Resources.GetResourceSet(new CultureInfo(culture), createIfNotExists: true,
            tryParents: false)?.GetString(key));

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void Every_catalogue_lever_has_a_translated_title(string langue)
    {
        // Temoin de lecture (18/09/2026) : « aucun levier sans titre » est aussi ce que rend un
        // catalogue vide. Sans cette ligne, un catalogue qui ne se charge plus rendrait ce test
        // vert en n'ayant regarde aucun levier.
        CatalogueDeCandidats.Ids.Should().HaveCountGreaterThan(10,
            "le crible doit avoir de vrais leviers a examiner");

        CatalogueDeCandidats.Ids
            .Where(id => TweakDisplayKeys.TitleKey(id) is var cle && (cle == id || !ExistsIn(langue, cle)))
            .Should().BeEmpty();
    }
}
