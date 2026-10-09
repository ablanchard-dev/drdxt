using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Les deux facons d'annoncer un gain qu'on n'a pas.
///
/// Additionner des pourcentages, et melanger des instruments. La seconde est la vraie : sommer
/// un gain de banc GPU et un gain de banc CPU produit un nombre qui n'a aucun referent, et il
/// sonne exactement comme les annonces que ce depot reproche aux autres.
/// </summary>
public class GainCumuleTests
{
    private const string BancGpu = "banc GPU deterministe";
    private const string BancCpu = "banc CPU soutenu";

    [Fact]
    public void Deux_gains_se_COMPOSENT_et_ne_s_additionnent_pas()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("gpu.core", BancGpu, 2.0, 0.122),
            new GainProuve("gpu.mem", BancGpu, 3.0, 0.122),
        ]);

        // 1,02 x 1,03 - 1 = 5,06 %, jamais 5,00 %.
        total.TotalPercent.Should().BeApproximately(5.06, 0.01);
        total.Retenus.Should().HaveCount(2);
    }

    [Fact]
    public void L_ecart_grossit_avec_le_nombre_de_leviers_et_c_est_pourquoi_la_composition_compte()
    {
        var cinq = Enumerable.Range(1, 5)
            .Select(i => new GainProuve($"levier{i}", BancGpu, 10, 0.5))
            .ToList();

        // L'addition naive dirait 50 %. La composition rend 61,05 %.
        GainCumule.Additionner(cinq).TotalPercent.Should().BeApproximately(61.05, 0.05);
    }

    [Fact]
    public void Deux_INSTRUMENTS_differents_ne_s_additionnent_JAMAIS()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("gpu.core", BancGpu, 2.2, 0.122),
            new GainProuve("cpu.budget", BancCpu, 10.3, 0.65),
        ]);

        total.TotalPercent.Should().BeApproximately(10.3, 0.01,
            "seul l'instrument retenu compte — l'autre gain n'est pas perdu, il est ECARTE");
        total.Retenus.Should().ContainSingle();
        total.Refus.Should().ContainSingle()
            .Which.Should().Contain("ne mesure pas la même chose",
                "le refus doit dire POURQUOI, sinon quelqu'un le contournera en croyant bien faire");
    }

    [Fact]
    public void Un_ecart_sous_son_propre_bruit_n_est_pas_un_gain()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("vrai", BancGpu, 5.0, 0.122),
            new GainProuve("bruit", BancGpu, 0.10, 0.122),
        ]);

        total.Retenus.Should().BeEquivalentTo(["vrai"]);
        total.Refus.Should().ContainSingle().Which.Should().Contain("bruit");
        total.TotalPercent.Should().BeApproximately(5.0, 0.01,
            "retenir le second ferait grossir le total avec du bruit — la definition d'un faux gain");
    }

    [Fact]
    public void Le_bruit_du_total_se_compose_DANS_LE_MEME_ESPACE_que_le_gain()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("a", BancCpu, 5, 0.6),
            new GainProuve("b", BancCpu, 5, 0.8),
        ]);

        // Ce test disait 1,00 JUSQU'AU 11/09/2026, et il NOMMAIT une garantie qu'il n'avait pas :
        // racine de (0,6^2 + 0,8^2) empile des POINTS de pourcentage alors que le total est un
        // Produit. Le bruit d'un produit se propage en relatif — racine de ((0,6/1,05)^2 +
        // (0,8/1,05)^2) = 0,952 % du total — puis se reporte a l'echelle du total : x 1,1025.
        total.BruitPercent.Should().BeApproximately(1.05, 0.01,
            "un total sans son bruit est un chiffre qu'on ne peut pas contester");
    }

    [Fact]
    public void L_ancienne_formule_SOUS_ESTIMAIT_le_bruit_et_c_est_la_direction_dangereuse()
    {
        // Verifie hors du depot par Monte-Carlo (400 000 tirages) : 3,27 %. L'empilement en
        // points de pourcentage rendait 2,24 %, soit 32 % de confiance de trop — sur un produit
        // dont la loi est « un ecart plus petit que son bruit n'est pas un gain », se tromper de ce
        // cote-la fait passer du bruit pour un gain.
        var cinq = Enumerable.Range(1, 5)
            .Select(i => new GainProuve($"levier{i}", BancGpu, 10, 1.0))
            .ToList();

        var total = GainCumule.Additionner(cinq);

        total.BruitPercent.Should().BeApproximately(3.274, 0.01);
        total.BruitPercent.Should().BeGreaterThan(Math.Sqrt(5),
            "l'empilement en points de pourcentage rendait exactement racine de 5");
    }

    [Fact]
    public void TROIS_LEVIERS_le_calcul_a_la_main_et_le_MODELE_DE_BRUIT_qu_il_choisit()
    {
        // ══ le cas, calcule a la main ═════════════════════════════════════════════════════════
        // Trois leviers sur le meme instrument, gains et bruits choisis pour que chaque chiffre
        // soit verifiable de tete :
        //
        //     levier      ecart      bruit      facteur f = 1 + ecart/100
        //     L1         +4,00 %    0,50 %      1,04
        //     L2         +3,00 %    0,40 %      1,03
        //     L3         +2,00 %    0,30 %      1,02
        //
        // ── LE GAIN : un produit, jamais une somme ────────────────────────────────────────────
        //     1,04 x 1,03            = 1,0712
        //     1,0712 x 1,02          = 1,092624
        //     total                  = (1,092624 - 1) x 100 = 9,2624 %
        //     l'addition naive dirait 4 + 3 + 2 = 9,00 % : 0,26 point de moins que la verite.
        //
        // ── Le bruit : quadrature, et voici pourquoi ──────────────────────────────────────────
        // Le code choisit le modele INDEPENDANT (racine de la somme des carres) et non le
        // modele CORRELE (somme directe). La raison n'est pas esthetique : chaque bruit est la
        // resolution d'une experience A/B SEPAREE — la dispersion passe-a-passe de ce levier-la,
        // mesuree dans ses propres passes. Deux experiences distinctes ne tirent pas la meme
        // erreur, donc leurs ecarts ne se renforcent pas systematiquement : ils se composent en
        // quadrature. Additionner les bruits reviendrait a supposer que toutes les mesures se
        // trompent dans le meme sens au meme moment.
        //
        // Et c'est le choix optimiste, ecrit ici plutot que decouvert plus tard. Si une derive
        // commune existe — temperature ambiante, etat du pilote, phase de partie — les erreurs
        // sont correlees et le vrai bruit monte jusqu'a la SOMME. Les deux bornes sont calculees
        // ci-dessous, et le test les tient toutes les deux : la quadrature est ce que le code
        // rend, la somme est le plafond qu'il n'atteint jamais.
        //
        // Le bruit se propage dans l'espace du produit — en relatif (sigma_i / f_i), puis reporte
        // a l'echelle du total (x F) :
        //     0,50 / 1,04 = 0,480769  ->  au carre 0,231139
        //     0,40 / 1,03 = 0,388350  ->  au carre 0,150815
        //     0,30 / 1,02 = 0,294118  ->  au carre 0,086505
        //     somme                    = 0,468460
        //     racine                   = 0,684441
        //     x 1,092624               = 0,747837 %
        //
        // `[Mesure]` Monte-Carlo hors du depot, 400 000 tirages sur ces trois leviers :
        // moyenne 9,2633 %, ecart-type **0,7478 %**. Le calcul a la main et le tirage s'accordent.
        //
        // Trois valeurs a ne pas confondre, et c'est tout l'interet de ce test :
        //     0,7478 %  <- ce que le code rend (quadrature dans l'espace du produit)
        //     0,7071 %  <- sqrt(0,5^2 + 0,4^2 + 0,3^2), la quadrature en POINTS de pourcentage,
        //                  l'ancienne formule corrigee le 11/09/2026 : elle sous-ESTIME
        //     1,2000 %  <- 0,5 + 0,4 + 0,3, le modele correle, plafond jamais atteint ici
        var total = GainCumule.Additionner(
        [
            new GainProuve("L1", BancGpu, 4.0, 0.5),
            new GainProuve("L2", BancGpu, 3.0, 0.4),
            new GainProuve("L3", BancGpu, 2.0, 0.3),
        ]);

        total.Retenus.Should().BeEquivalentTo(["L1", "L2", "L3"]);
        total.TotalPercent.Should().BeApproximately(9.2624, 0.0005,
            "le gain se COMPOSE : 1,04 x 1,03 x 1,02 - 1, jamais 4 + 3 + 2");
        total.BruitPercent.Should().BeApproximately(0.747837, 0.0005,
            "le bruit se propage en relatif puis se reporte a l'echelle du total");

        // Les deux simplifications que ce test existe pour faire rougir. Elles sont ecrites en
        // clair : celui qui « simplifie » la formule dans six mois retombera sur l'une des deux, et
        // c'est ce test-ci qui le lui dira, pas un test generique.
        total.BruitPercent.Should().BeGreaterThan(Math.Sqrt(0.5 * 0.5 + 0.4 * 0.4 + 0.3 * 0.3),
            "empiler les bruits en POINTS de pourcentage rend 0,7071 % et sous-estime l'incertitude");
        total.BruitPercent.Should().BeLessThan(0.5 + 0.4 + 0.3,
            "le modele CORRELE (somme directe) est le plafond : le code suppose l'independance");
    }

    [Fact]
    public void Sur_UN_SEUL_gain_les_deux_formules_coincident_et_le_chiffre_publie_ne_bouge_pas()
    {
        // Le seul gain prouve de cette machine. Une correction d'incertitude qui deplacerait un
        // chiffre deja publie serait une dette, pas une correction : elle n'en deplace aucun.
        var total = GainCumule.Additionner([new GainProuve("gpu.core", BancGpu, 2.2, 0.122)]);

        total.TotalPercent.Should().BeApproximately(2.2, 0.001);
        total.BruitPercent.Should().BeApproximately(0.122, 0.0005);
    }

    [Fact]
    public void Une_perte_de_CENT_POUR_CENT_ne_se_compose_pas_et_le_refus_se_dit()
    {
        // Le facteur tombe a zero : le total n'est plus compose, il est ecrase, et son incertitude
        // relative n'existe plus. Ce n'est pas une mesure de performance.
        var total = GainCumule.Additionner(
        [
            new GainProuve("gagnant", BancGpu, 4.0, 0.122),
            new GainProuve("ecrase", BancGpu, -100, 0.122),
        ]);

        total.Retenus.Should().BeEquivalentTo(["gagnant"]);
        total.Refus.Should().ContainSingle().Which.Should().Contain("ne se compose pas");
        total.BruitPercent.Should().BeGreaterThan(0).And.NotBe(double.NaN);
    }

    [Fact]
    public void Une_perte_compte_comme_une_perte_et_ne_se_cache_pas()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("gagnant", BancGpu, 4.0, 0.122),
            new GainProuve("perdant", BancGpu, -2.0, 0.122),
        ]);

        // 1,04 x 0,98 - 1 = 1,92 %.
        total.TotalPercent.Should().BeApproximately(1.92, 0.01,
            "un levier qui COUTE de la performance doit tirer le total vers le bas");
    }

    [Fact]
    public void Aucun_gain_prouve_rend_zero_et_le_DIT()
    {
        var vide = GainCumule.Additionner(null);

        vide.TotalPercent.Should().Be(0);
        vide.Refus.Should().ContainSingle().Which.Should().Contain("aucun gain prouve");
        vide.Instrument.Should().BeNull("on ne nomme pas un instrument qu'on n'a pas utilise");
    }

    // ── La campagne : ce que plusieurs leviers ont laisse dans la machine ──────────────────
    // Des DOUBLES, jamais une campagne reelle : aucun de ces tests n'ecrit sur la machine.

    private static CandidateResult Candidat(
        string id, CandidateOutcome issue, GameMetricKind metrique, double ecart, double resolution)
        => new(id, issue, null, null, null,
            [new MetricEvidence(metrique, Evidence.From(ecart, resolution))]);

    [Fact]
    public void Un_candidat_REJETE_ne_compte_PAS_dans_le_total_car_il_a_ete_annule()
    {
        var total = GainCumule.DepuisLaCampagne(
        [
            Candidat("garde", CandidateOutcome.Kept, GameMetricKind.Moyenne, 4.0, 0.5),
            Candidat("annule", CandidateOutcome.RejectedRegression, GameMetricKind.Moyenne, 9.0, 0.5),
        ], GameMetricKind.Moyenne);

        total.Retenus.Should().BeEquivalentTo(["garde"]);
        total.TotalPercent.Should().BeApproximately(4.0, 0.01,
            "son ecart etait reel, il n'est simplement plus dans la machine — le compter annoncerait "
            + "un total que le PC ne rend pas");
    }

    [Fact]
    public void Une_preuve_NON_RESOLUE_n_entre_pas_au_total_meme_si_le_levier_est_garde()
    {
        var total = GainCumule.DepuisLaCampagne(
        [
            Candidat("prouve", CandidateOutcome.Kept, GameMetricKind.UnPourCentLow, 12.0, 4.0),
            Candidat("sous le bruit", CandidateOutcome.Kept, GameMetricKind.UnPourCentLow, 2.0, 4.0),
        ], GameMetricKind.UnPourCentLow);

        total.Retenus.Should().BeEquivalentTo(["prouve"]);
        total.Instrument.Should().Be("1 % low");
    }

    [Fact]
    public void Un_levier_garde_ailleurs_peut_TIRER_CETTE_grandeur_vers_le_bas_et_le_total_le_dit()
    {
        // Le cas qui interdit un composite : garde pour ses FPS moyens, il degrade le 1 % low.
        // 1,08 x 0,94 - 1 = +1,52 % : le total baisse, il ne se cache pas.
        var total = GainCumule.DepuisLaCampagne(
        [
            new CandidateResult("levier", CandidateOutcome.Kept, null, null, null,
            [
                new MetricEvidence(GameMetricKind.Moyenne, Evidence.From(8.0, 1.0)),
                new MetricEvidence(GameMetricKind.UnPourCentLow, Evidence.From(-6.0, 1.0)),
            ]),
            Candidat("autre", CandidateOutcome.Kept, GameMetricKind.UnPourCentLow, 8.0, 1.0),
        ], GameMetricKind.UnPourCentLow);

        total.TotalPercent.Should().BeApproximately(1.52, 0.01);
        GainCumule.Phrase(total).Should().StartWith("PROUVÉ");
    }

    [Fact]
    public void Le_VERDICT_tient_en_une_phrase_et_dit_PAS_PROUVE_sous_le_bruit_du_TOTAL()
    {
        // Chaque levier prouve le sien, et le total ne prouve rien. +2,00 % passe son bruit de
        // 1,90 %, -1,90 % passe le sien de 1,80 % : tous deux sont retenus. Mais ils se compensent —
        // 1,02 x 0,981 - 1 = +0,06 % — pendant que leurs bruits, eux, s'ajoutent en quadrature et
        // donnent +/-2,62 %. Le total se juge contre SON bruit, jamais contre celui d'un levier.
        var sousLeBruit = GainCumule.Additionner(
        [
            new GainProuve("a", BancGpu, 2.0, 1.9),
            new GainProuve("b", BancGpu, -1.9, 1.8),
        ]);

        sousLeBruit.Retenus.Should().HaveCount(2, "chacun a passe son propre bruit");
        // La phrase le dit sans qu'on ait a comparer deux nombres soi-meme.
        GainCumule.Phrase(sousLeBruit).Should().StartWith("PAS PROUVÉ");
        GainCumule.Phrase(sousLeBruit).Should().Contain("ne dépasse pas le bruit du total");
    }

    [Fact]
    public void Une_campagne_SANS_aucun_levier_garde_dit_RIEN_DE_PROUVE_et_nomme_la_grandeur()
    {
        var total = GainCumule.DepuisLaCampagne(
            [Candidat("annule", CandidateOutcome.Unstable, GameMetricKind.Moyenne, 9.0, 0.5)],
            GameMetricKind.Moyenne);

        GainCumule.Phrase(total).Should().StartWith("RIEN DE PROUVÉ");
        GainCumule.Phrase(total).Should().Contain("FPS moyens",
            "la phrase nomme la grandeur — une seule fois : elle l'a repetee deux fois au rejeu du 11/09");
        System.Text.RegularExpressions.Regex.Matches(GainCumule.Phrase(total), "FPS moyens")
            .Should().ContainSingle();
    }

    [Fact]
    public void Quand_tous_les_gains_sont_du_bruit_le_total_est_zero_avec_ses_raisons()
    {
        var total = GainCumule.Additionner(
        [
            new GainProuve("a", BancGpu, 0.05, 0.122),
            new GainProuve("b", BancGpu, 0.10, 0.122),
        ]);

        total.TotalPercent.Should().Be(0);
        total.Retenus.Should().BeEmpty();
        total.Refus.Should().HaveCount(2, "chaque refus se dit, aucun ne se resume");
    }
}
