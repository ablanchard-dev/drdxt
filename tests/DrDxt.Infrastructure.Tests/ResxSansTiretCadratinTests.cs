using System.Collections;
using System.Globalization;
using System.Resources;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// Ce que le client lit ne porte pas de tiret cadratin.
///
/// Retour du 16/09/2026 : les textes sont justes, mais les « — » a chaque phrase les font lire comme
/// generes. On ponctue comme on ecrit : deux-points, virgule, point. Ce gate lit les deux langues
/// dans le jeu de ressources lui-meme, il grandit tout seul avec la prochaine cle.
///
/// Seule exception possible, declaree : une valeur qui n'est que le tiret, placeholder de cellule vide.
/// 27/09/2026 : la derniere (dashboard.value.unavailable) etait un texte que plus rien n'affichait, retiree.
/// </summary>
public sealed class ResxSansTiretCadratinTests
{
    private static readonly HashSet<string> CellulesVides = new(StringComparer.Ordinal) { };

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Aucun_texte_affiche_ne_contient_de_tiret_cadratin(string langue)
    {
        var manager = new ResourceManager("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        var jeu = manager.GetResourceSet(new CultureInfo(langue), createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException("jeu de ressources introuvable : " + langue);

        // Temoin de lecture (18/09/2026) : « aucun fautif » est aussi ce que rend un jeu de
        // ressources vide. Sans cette ligne, un resx qui ne se charge plus laisserait tout passer.
        jeu.Cast<DictionaryEntry>().Count(e => e.Value is string).Should().BeGreaterThan(100,
            "le crible doit avoir de vrais textes a lire");

        var fautifs = jeu.Cast<DictionaryEntry>()
            .Where(e => e.Value is string v && (v.Contains('—') || v.Contains(" – "))
                        && !CellulesVides.Contains((string)e.Key))
            .Select(e => (string)e.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        fautifs.Should().BeEmpty("les tirets cadratins font lire le texte comme genere");
    }
}
