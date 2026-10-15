using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le relevé des parties additionne les processus d'un même programme avant le seuil, et rien n'est coupé.
// Filtrés un par un à 5 % puis coupés à 5, douze onglets de navigateur à 6 % (72 % d'un cœur) donneraient
// « 30 % », et à 4 % chacun le navigateur disparaîtrait.
public sealed class VoisinsParNomTests
{
    [Fact]
    public void Les_processus_d_un_meme_nom_s_additionnent_avant_le_seuil()
    {
        var lus = Enumerable.Range(0, 12).Select(i => new VoisinDuJeu("chrome", 4, null, 100 + i))
            .Concat([new VoisinDuJeu("obs64", 30, 2, 7), new VoisinDuJeu("a", 6, null, 8), new VoisinDuJeu("b", 6, null, 9),
                     new VoisinDuJeu("c", 6, null, 10), new VoisinDuJeu("d", 6, null, 11), new VoisinDuJeu("calme", 1, null, 12)])
            .ToList();

        var parNom = VoisinsWin32.ParNom(lus);

        parNom.Should().HaveCount(6, "aucune coupe à 5 ; seul le programme sous le seuil disparaît");
        parNom[0].Should().Be(new VoisinDuJeu("chrome", 48, null, 0));
        parNom.Select(v => v.Nom).Should().Contain("obs64").And.NotContain("calme");
    }
}
