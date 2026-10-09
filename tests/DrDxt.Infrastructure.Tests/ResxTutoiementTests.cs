using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// Le produit tutoie le joueur. Le 16/09/2026, 68 textes francais le vouvoyaient encore (licence, BIOS,
/// application des reglages) : deux voix dans la meme appli, au milieu d'un meme ecran.
/// </summary>
public sealed class ResxTutoiementTests
{
    [Fact]
    public void Aucun_texte_francais_ne_vouvoie_le_joueur()
    {
        var manager = new ResourceManager("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        var jeu = manager.GetResourceSet(new CultureInfo("fr"), createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException("jeu de ressources francais introuvable");

        // Temoin de lecture (18/09/2026) : « aucun fautif » est aussi ce que rend un jeu de
        // ressources vide. Sans cette ligne, un resx qui ne se charge plus laisserait tout passer.
        jeu.Cast<DictionaryEntry>().Count(e => e.Value is string).Should().BeGreaterThan(100,
            "le crible doit avoir de vrais textes a lire");

        var fautifs = jeu.Cast<DictionaryEntry>()
            .Where(e => e.Value is string v && Regex.IsMatch(v, @"\b([Vv]ous|[Vv]otre|[Vv]os|\w+ez(-le|-la)?)\b(?<!\b(chez|assez))"))
            .Select(e => (string)e.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        fautifs.Should().BeEmpty();
    }
}
