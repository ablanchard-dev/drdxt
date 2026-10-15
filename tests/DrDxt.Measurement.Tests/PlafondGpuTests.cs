using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Le plafond qui contredit une annonce que j'ai failli faire.
///
/// Apres avoir mesure que le processeur tombe de 5 292 a 3 698 MHz sous charge
/// soutenue, la conclusion tentante etait « lever la limite rendrait +25 a +30 % d'images ». Le
/// meme jeu de mesures la contredit : le GPU est occupe 86 a 95 % du temps de trame, donc il ne
/// peut pas en dessiner 30 % de plus. Ces tests figent le calcul qui l'interdit.
/// </summary>
public class PlafondGpuTests
{
    [Theory]
    [InlineData(90, 11.11)]   // le cas courant de la machine de reference
    [InlineData(95, 5.26)]    // scene la plus chargee : presque aucune marge
    [InlineData(86, 16.28)]   // scene la plus legere : la marge la plus grande mesuree
    [InlineData(50, 100.0)]   // un GPU a moitie oisif pourrait doubler
    public void La_borne_vient_de_l_occupation_GPU_et_de_rien_d_autre(double busy, double attendu)
        => PlafondGpu.Borner(busy).PlafondPercent.Should().BeApproximately(attendu, 0.05);

    [Fact]
    public void Un_GPU_deja_sature_ne_rend_RIEN_de_plus()
    {
        var borne = PlafondGpu.Borner(100);

        borne.PlafondPercent.Should().Be(0);
        borne.Lisible.Should().Contain("déjà saturé",
            "annoncer un gain CPU sur un GPU sature serait promettre des images que rien ne peut dessiner");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(140)]
    public void Une_occupation_absurde_ne_produit_aucune_borne(double busy)
    {
        var borne = PlafondGpu.Borner(busy);

        borne.PlafondPercent.Should().Be(0);
        borne.Lisible.Should().Contain("hors de",
            "une entree invalide se dit, elle ne se replie pas sur une borne plausible");
    }

    [Fact]
    public void C_est_l_ETENDUE_qui_se_publie_et_pas_une_borne_unique()
    {
        // Les six occupations GPU reellement mesurees le 09/09 sur Warzone.
        var mesurees = new double[] { 86, 87, 90, 91, 94, 95 };

        var (basse, haute) = PlafondGpu.Etendue(mesurees);

        basse.PlafondPercent.Should().BeApproximately(5.26, 0.05, "la scene la plus chargee");
        haute.PlafondPercent.Should().BeApproximately(16.28, 0.05, "la scene la plus legere");
        haute.PlafondPercent.Should().BeLessThan(25,
            "meme la scene la PLUS favorable interdit l'annonce « +25 a +30 % » : "
            + "c'est ce test qui rend cette phrase impossible a ecrire de bonne foi");
    }

    [Fact]
    public void Aucune_serie_exploitable_rend_zero_et_le_DIT()
    {
        var (basse, haute) = PlafondGpu.Etendue([0, -1, 200]);

        basse.PlafondPercent.Should().Be(0);
        haute.Lisible.Should().Contain("aucune série exploitable");
        PlafondGpu.Etendue(null).Basse.PlafondPercent.Should().Be(0);
    }

    [Fact]
    public void Les_series_invalides_sont_ecartees_sans_faire_tomber_les_bonnes()
    {
        var (basse, haute) = PlafondGpu.Etendue([95, 0, 86, 300]);

        basse.PlafondPercent.Should().BeApproximately(5.26, 0.05);
        haute.PlafondPercent.Should().BeApproximately(16.28, 0.05);
    }

    // ── La borne tiree d'une mesure de scan ────────────────────────────────────────────────
    // L'occupation vient de `BenchmarkResult.GpuBusyPercent`, que le produit publie deja pour son
    // verdict de goulot : ce type ne refait pas l'arithmetique. Ce qui compte ici, c'est que la
    // ligne se TAISE quand rien n'a ete mesure.

    [Fact]
    public void L_occupation_sort_de_la_mesure_et_pas_d_une_constante()
    {
        // L'occupation vient de `BenchmarkResult.GpuBusyPercent`, appariee trame par trame :
        // ici 90 % ⇒ +11,1 % au plus. Ce type ne la recalcule pas.
        var borne = PlafondGpu.DepuisLaMesure(90);

        borne!.GpuBusyPercent.Should().BeApproximately(90, 0.01);
        borne.PlafondPercent.Should().BeApproximately(11.11, 0.05);
        borne.Lisible.Should().Contain("tu ne gagnerais au mieux que");
    }

    [Fact]
    public void Sans_mesure_GPU_il_n_y_a_PAS_de_borne()
    {
        // Le cas banal : aucun jeu lance, scan degrade. Une borne par defaut serait lue comme
        // « voila ce que tu peux gagner », c'est-a-dire une promesse tiree de rien.
        PlafondGpu.DepuisLaMesure(null).Should().BeNull("la passe n'a pas mesure de GPU");
        PlafondGpu.DepuisLaMesure(0).Should().BeNull("zero pour cent d'occupation n'est pas une "
                                                     + "mesure, c'est une absence");
        PlafondGpu.DepuisLaMesure(-3).Should().BeNull();
    }

    [Fact]
    public void Un_GPU_LARGEMENT_libre_ne_promet_aucun_chiffre()
    {
        // Le defaut trouve en lançant l'outil. Capture reelle du compositeur de bureau, GPU
        // occupe a 1,4 % : la phrase annonçait « pas plus de 6 945,4 % d'images de plus ». Vrai,
        // inutile, et lu comme une promesse a quatre chiffres.
        var borne = PlafondGpu.DepuisLaMesure(1.4);

        borne!.Lisible.Should().Contain("ce n'est pas elle qui limite");
        borne.Lisible.Should().NotContain("plus de 6",
            "un plafond a quatre chiffres n'est pas un plafond, c'est une promesse");
        borne.PlafondPercent.Should().BeGreaterThan(1000,
            "le nombre reste disponible au code ; c'est la PHRASE qui refuse de le dire");
    }

    [Fact]
    public void Un_GPU_qui_ne_decroche_JAMAIS_se_dit_sature_au_lieu_de_se_taire()
    {
        // Le travail GPU peut se chevaucher d'une trame a l'autre : le rapport passe au-dessus
        // de 1. Se taire laisserait un joueur dont le GPU est le mur chercher du cote CPU.
        var borne = PlafondGpu.DepuisLaMesure(120);

        borne!.PlafondPercent.Should().Be(0);
        borne.Lisible.Should().Contain("déjà saturé");
    }
}
