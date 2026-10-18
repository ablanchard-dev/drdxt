using DrDxt.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

// La courbe des temps de trame de CETTE mesure (cahier P1, affichage). Pas un avant/après : l'App n'a qu'une passe.
public sealed class CourbeDesTempsDeTrameTests
{
    [Fact]
    public void Chaque_colonne_garde_la_trame_la_plus_lente_pour_que_les_a_coups_se_voient()
    {
        var images = Enumerable.Repeat(10.0, 1000).ToArray();
        images[350] = 50;   // un à-coup dans la quatrième colonne (trames 300 à 399)

        var c = CourbeDesTempsDeTrame.Construire(images, colonnes: 10, largeur: 900, hauteur: 100)!;

        c.Points.Should().HaveCount(10);
        c.Points.First().X.Should().Be(0);
        c.Points.Last().X.Should().Be(900);
        c.Points.MinBy(p => p.Y).Should().Be(c.Points[3], "l'à-coup est le point le plus haut de la courbe");
        c.MaxMs.Should().Be(50);
    }

    // La marge de la mesure est l'incertitude de la MOYENNE des FPS entre passes ; la courbe trace la trame la plus lente de
    // chaque instant. Mises ensemble, presque toute la courbe sortait d'une « bande de bruit » : des à-coups partout, à tort.
    // La légende ne parle donc d'aucune bande.
    [Theory]
    [InlineData("fr-FR", "bande")]
    [InlineData("fr-FR", "bruit")]
    [InlineData("en-US", "band")]
    [InlineData("en-US", "noise")]
    public void La_legende_ne_compare_pas_la_courbe_a_une_bande_de_bruit(string langue, string mot)
        => new DrDxt.Infrastructure.Localization.ResxLocalizer()
            .Get("Scan.Courbe.Legende", System.Globalization.CultureInfo.GetCultureInfo(langue)).Should().NotContain(mot);

    // Sans le dire, le joueur lirait des FPS : la légende nomme ce que montre la courbe et la ligne pointillée.
    [Theory]
    [InlineData("fr-FR", "la trame la plus lente", "pointillée est la médiane")]
    [InlineData("en-US", "the slowest frame", "dotted line is the median")]
    public void La_legende_dit_ce_que_montrent_la_courbe_et_la_ligne(string langue, string courbe, string ligne)
        => new DrDxt.Infrastructure.Localization.ResxLocalizer()
            .Get("Scan.Courbe.Legende", System.Globalization.CultureInfo.GetCultureInfo(langue)).Should().Contain(courbe).And.Contain(ligne);

    [Fact]
    public void La_mediane_se_trace_a_sa_hauteur_dans_l_echelle_des_trames_les_plus_lentes()
    {
        double[] images = [10, 10, 10, 20];   // médiane 10 ; trame la plus lente 20, échelle 22 ms pour 110 pixels

        var c = CourbeDesTempsDeTrame.Construire(images, colonnes: 4, largeur: 900, hauteur: 110)!;

        c.MedianeY.Should().BeApproximately(110 - 50, 1e-9);
    }

    // Un pic isolé (500 ms) écrasait toute la courbe : l'axe va au 99e centile des points tracés, et ce qui le dépasse se
    // dessine écrêté en haut, marqué et compté. Rien n'est caché sans être dit.
    [Fact]
    public void Les_pics_au_dessus_du_99e_centile_sont_ecretes_marques_et_comptes()
    {
        var images = Enumerable.Repeat(10.0, 300).ToArray();
        images[100] = 500;
        images[200] = 480;

        var c = CourbeDesTempsDeTrame.Construire(images, colonnes: 300, largeur: 600, hauteur: 110)!;

        c.PicsEcretes.Should().HaveCount(2);
        c.PicsEcretes.Should().OnlyContain(p => p.Y == 0, "écrêtés en haut de la courbe");
        c.MaxMs.Should().Be(500, "le plus haut pic reste dit");
        c.Points[0].Y.Should().BeApproximately(110 - 100, 1e-9, "échelle : 99e centile (10 ms) plus 10 % = 11 ms");
    }

    // Écrêtés, les pics se disent : leur nombre et le plus haut, accordés. Sans pic, pas de ligne.
    [Fact]
    public void Les_pics_ecretes_se_disent_avec_leur_nombre_et_le_plus_haut()
    {
        var images = Enumerable.Repeat(10.0, 300).ToArray();
        images[100] = 500;
        images[200] = 480;
        string L(string k) => k == "Scan.Courbe.Pics" ? "{0} pic(s) marqué(s) ; le plus haut : {1} ms" : k;

        ScanViewModel.LignePics(CourbeDesTempsDeTrame.Construire(images), L).Should().Be("2 pics marqués ; le plus haut : 500 ms");
        ScanViewModel.LignePics(CourbeDesTempsDeTrame.Construire(Enumerable.Repeat(10.0, 300).ToArray()), L).Should().BeNull();
        ScanViewModel.LignePics(null, L).Should().BeNull();
    }

    [Fact]
    public void Sans_pic_rien_n_est_ecrete()
        => CourbeDesTempsDeTrame.Construire(Enumerable.Repeat(10.0, 300).ToArray())!.PicsEcretes.Should().BeEmpty();

    [Fact]
    public void Sans_trames_pas_de_courbe()
    {
        CourbeDesTempsDeTrame.Construire([]).Should().BeNull();
        CourbeDesTempsDeTrame.Construire(null).Should().BeNull();
    }
}
