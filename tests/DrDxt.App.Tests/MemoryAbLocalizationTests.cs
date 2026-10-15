using System.Globalization;
using System.Resources;
using DrDxt.App.ViewModels;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// I18n de la section « ton passage dans le BIOS », moitie memoire.
//
// ATTENTION, meme piege que le tableau de bord : `ILocalizer.Get(cle, "fr")` passe par
// `ResourceManager`, qui RETOMBE sur la culture neutre. Une cle absente du resx FR rendrait
// tranquillement la phrase anglaise, et un test « valeur != cle » resterait vert.
// Le seul controle etanche est de lire le jeu de ressources sans remontee aux parents.
// -----------------------------------------------------------------------
public sealed class MemoryAbLocalizationTests
{
    private static readonly ResourceManager Resources =
        new("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private static bool ExistsIn(string culture, string key)
    {
        var set = Resources.GetResourceSet(new CultureInfo(culture), createIfNotExists: true, tryParents: false);
        return !string.IsNullOrEmpty(set?.GetString(key));
    }

    [Theory]
    [InlineData("Scan.BiosReturn.Header")]
    [InlineData(MemoryAbStringKeys.Header)]
    [InlineData(MemoryAbStringKeys.Bandwidth)]
    [InlineData(MemoryAbStringKeys.Latency)]
    [InlineData(MemoryAbStringKeys.UnitGBs)]
    [InlineData(MemoryAbStringKeys.UnitNs)]
    [InlineData(MemoryAbStringKeys.Significant)]
    [InlineData(MemoryAbStringKeys.NotSignificant)]
    [InlineData(MemoryAbStringKeys.NoVerdictBusy)]
    [InlineData(MemoryAbStringKeys.NoVerdictNoRoom)]
    [InlineData(MemoryAbStringKeys.NoVerdictFailed)]
    [InlineData(MemoryAbStringKeys.NoVerdictStale)]
    [InlineData(MemoryAbStringKeys.NoVerdictGame)]
    public void Chaque_cle_existe_en_anglais_ET_en_francais(string key)
    {
        ExistsIn("en", key).Should().BeTrue($"'{key}' doit exister en anglais");
        ExistsIn("fr", key).Should().BeTrue($"'{key}' doit exister en francais, sans retomber sur l'anglais");
    }

    [Fact]
    public void La_phrase_du_refus_porte_bien_son_emplacement_de_raison()
    {
        // Sans le {0}, le client lit « une des deux mesures n'a pas ete acceptee () » et n'apprend
        // jamais LAQUELLE des quatre gardes a parle.
        foreach (var culture in new[] { "en", "fr" })
        foreach (var cle in new[] { MemoryAbStringKeys.NoVerdictBusy, MemoryAbStringKeys.NoVerdictNoRoom, MemoryAbStringKeys.NoVerdictFailed, MemoryAbStringKeys.NoVerdictStale, MemoryAbStringKeys.NoVerdictGame })
        {
            var set = Resources.GetResourceSet(new CultureInfo(culture), true, false);
            set!.GetString(cle).Should().Contain("{0}");
        }
    }

    [Fact]
    public void Aucune_phrase_de_cette_section_ne_dit_leger_gain()
    {
        // La formule que tous les optimiseurs du marche se permettent. Un ecart sous le bruit est
        // une ABSENCE de resultat ; l'appeler « petit gain » est la seule chose que cette section
        // n'a pas le droit de faire.
        var fr = Resources.GetResourceSet(new CultureInfo("fr"), true, false)!;
        var en = Resources.GetResourceSet(new CultureInfo("en"), true, false)!;

        fr.GetString(MemoryAbStringKeys.NotSignificant).Should().Contain("absence de résultat");
        en.GetString(MemoryAbStringKeys.NotSignificant).Should().Contain("absence of a result");
    }

    [Fact]
    public void Seule_la_faute_d_INSTRUMENT_a_le_droit_d_ecarter_les_programmes_ouverts()
    {
        // « Fermer des applications n'y changerait rien » est vrai quand l'instrument mesure la
        // mauvaise chose. C'est faux quand le banc n'a pas pu allouer ses 384 Mo : la, fermer des
        // programmes est le geste qui repare. Une phrase qui exclut la bonne cause est pire que
        // pas de phrase.
        foreach (var culture in new[] { "en", "fr" })
        {
            var set = Resources.GetResourceSet(new CultureInfo(culture), true, false)!;
            var exclusion = culture == "fr" ? "programmes ouverts" : "open programs";

            set.GetString(MemoryAbStringKeys.NoVerdictFailed).Should().Contain(exclusion);
            set.GetString(MemoryAbStringKeys.NoVerdictNoRoom).Should().NotContain(exclusion);
        }
    }
}
