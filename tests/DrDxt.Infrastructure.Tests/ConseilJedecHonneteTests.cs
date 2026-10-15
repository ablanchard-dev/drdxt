using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// Le conseil « repasser en JEDEC pour voir la différence avec EXPO/XMP » ne dit ni « quelques jours »
/// ni « la mémoire est hors de cause ». Deux erreurs à éviter :
/// avec un arrêt tous les neuf jours environ, une semaine sans arrêt arrive par hasard presque une fois
/// sur deux ; et le JEDEC n'innocente que le profil, pas les barrettes, qui se testent autrement.
/// </summary>
public sealed class ConseilJedecHonneteTests
{
    private const string Cle = "Finding.Memory.XmpOnWithUncleanShutdowns";

    [Theory]
    [InlineData("fr", "quelques jours", "hors de cause", "écart habituel", "test mémoire")]
    [InlineData("en", "a few days", "the memory is cleared", "usual gap", "memory test")]
    // Des arrêts qui cessent rendent le profil probable, pas coupable.
    [InlineData("fr", "c’était le profil", "hors de cause", "suspect le plus probable", "test mémoire")]
    [InlineData("en", "it was the profile", "the memory is cleared", "most likely suspect", "memory test")]
    public void Le_conseil_ne_conclut_ni_trop_vite_ni_trop_large(string langue, string tropCourt, string tropLarge,
        string duree, string autreTest)
    {
        var texte = new ResxLocalizer().Get(Cle, new CultureInfo(langue));

        texte.Should().NotContain(tropCourt).And.NotContain(tropLarge);
        texte.Should().Contain(duree).And.Contain(autreTest);
    }

    // Le bilan de stabilite lit desormais le resultat du test integre a Windows ; le
    // conseil doit nommer ce test-la, celui dont le produit sait rendre le resultat.
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Le_conseil_nomme_le_test_integre_a_Windows(string langue)
        => new ResxLocalizer().Get(Cle, new CultureInfo(langue)).Should().Contain("mdsched.exe");

    // Signalé par Remarque du joueur : « a pas vu que dans mon BIOS mon vrai profil XMP pas activé ».
    // Sa mémoire (kit 4000) tourne à 3800 : au-dessus du standard JEDEC, sous la vitesse du kit.
    // DrDXT mesure l'overclock, il ne sait pas si c'est le profil du kit ou un réglage à la main :
    // la phrase ne peut pas affirmer qu'un profil est actif.
    [Theory]
    [InlineData("fr", "un profil XMP/EXPO est actif", "au-dessus de la vitesse standard JEDEC")]
    [InlineData("en", "an XMP/EXPO profile is active", "above the JEDEC standard speed")]
    public void Le_conseil_dit_ce_qui_est_mesure_et_pas_un_profil_qu_il_n_a_pas_lu(string langue, string affirmation, string mesure)
    {
        var texte = new ResxLocalizer().Get(Cle, new CultureInfo(langue));

        texte.Should().NotContain(affirmation).And.Contain(mesure);
    }
}
