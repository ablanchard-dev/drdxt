using DrDxt.Measurement.Memory;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests.Memory;

public class MemoryQualityTests
{
    // Une mesure saine de reference : celle de la machine du 06/09 (DDR4-3800 double canal,
    // pic theorique 60,8 Go/s). Chaque test degrade un seul champ, pour qu'un echec designe
    // le controle qui l'a attrape.
    private static MemoryMeasurement Saine() => new(
        BandwidthGBs: 34.0,
        BandwidthSigmaPct: 1.83,
        LatencyNs: 66.0,
        LatencySigmaPct: 3.45,
        Staircase: new[]
        {
            new StaircasePoint(16 * 1024, 1.2),
            new StaircasePoint(512 * 1024, 4.0),
            new StaircasePoint(8 * 1024 * 1024, 14.0),
            new StaircasePoint(128 * 1024 * 1024, 66.0),
        });

    [Fact]
    public void La_mesure_reellement_relevee_le_06_09_est_acceptee()
    {
        // Le garde-fou inverse des quatre suivants : un controle trop severe rejetterait tout, et
        // un instrument qui refuse toujours ne prouve rien de plus qu'un instrument qui accepte
        // toujours. Ces chiffres viennent de la machine, pas d'une intention.
        var verdict = MemoryQuality.Check(Saine(), theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeTrue();
        verdict.RejectionId.Should().BeNull();
    }

    [Fact]
    public void Un_debit_superieur_au_pic_theorique_est_rejete_car_physiquement_impossible()
    {
        var menteuse = Saine() with { BandwidthGBs = 61.0 };

        var verdict = MemoryQuality.Check(menteuse, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-01");
    }

    [Fact]
    public void Une_latence_de_25_ns_est_rejetee_car_c_est_du_cache_et_non_de_la_DRAM()
    {
        // Une chaine de pointeurs de 16 Mo tient dans un L3 de 33 Mo et rend 25 ns (mesure). Aucune
        // DRAM ne repond en 25 ns : c'est ce chiffre trop beau qui trahit l'erreur, et le controle
        // doit la voir tout seul.
        var duCache = Saine() with { LatencyNs = 25.0 };

        var verdict = MemoryQuality.Check(duCache, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-04");
    }

    [Theory]
    [InlineData(9.0, 3.45)]  // le debit part en vrille
    [InlineData(1.83, 8.0)]  // la latence part en vrille
    public void Une_dispersion_au_dela_de_5_pourcent_est_rejetee_car_la_machine_n_est_pas_au_repos(
        double bandwidthSigmaPct, double latencySigmaPct)
    {
        // C'est la parade au piege de la mesure de reference : prise sur une machine chargee, elle
        // resterait fausse pour toujours. Une mesure trop dispersee n'est pas stockee.
        var agitee = Saine() with
        {
            BandwidthSigmaPct = bandwidthSigmaPct,
            LatencySigmaPct = latencySigmaPct,
        };

        var verdict = MemoryQuality.Check(agitee, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-03");
    }

    [Fact]
    public void Un_escalier_qui_redescend_est_rejete_car_une_hierarchie_de_caches_ne_redescend_pas()
    {
        // Une zone plus grande ne peut pas etre plus RAPIDE a parcourir : chaque palier deborde
        // d'un cache de plus. Une courbe qui redescend ne decrit pas une memoire, elle decrit une
        // mesure perturbee ou un parcours qui n'a pas la forme annoncee.
        var aLEnvers = Saine() with
        {
            Staircase = new[]
            {
                new StaircasePoint(16 * 1024, 1.2),
                new StaircasePoint(512 * 1024, 4.0),
                new StaircasePoint(8 * 1024 * 1024, 30.0),
                new StaircasePoint(128 * 1024 * 1024, 20.0),
            },
        };

        var verdict = MemoryQuality.Check(aLEnvers, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-02");
    }

    [Fact]
    public void Un_creux_de_quelques_DIXIEMES_de_nanoseconde_sur_les_petites_fenetres_est_du_bruit()
    {
        // Le pire genre de defaut : l'instrument verrait une chute de debit de 89 % et REFUSERAIT de la
        // dire, en Q-02, pour un creux de 0,2 ns entre 32 et 48 Ko. A cette echelle, la resolution du chronometre et le
        // cout de la boucle pesent plus que la memoire : un ecart relatif y est enorme et ne veut
        // rien dire. Un refus doit etre grand dans les deux sens -- en pourcentage et en valeur.
        var vraieCourbe = Saine() with
        {
            Staircase = new[]
            {
                new StaircasePoint(32 * 1024, 1.20),
                new StaircasePoint(48 * 1024, 0.97),   // -19 %, mais seulement 0,23 ns
                new StaircasePoint(256 * 1024, 2.70),
                new StaircasePoint(2 * 1024 * 1024, 5.83),
                new StaircasePoint(128 * 1024 * 1024, 76.69),
            },
        };

        MemoryQuality.Check(vraieCourbe, theoreticalPeakGBs: 60.8).Accepted.Should().BeTrue();
    }

    [Fact]
    public void Mais_un_recul_de_SEIZE_nanosecondes_reste_un_refus()
    {
        // Le contre-controle : sans lui, on ferait passer le test precedent en supprimant Q-02.
        var vraiRecul = Saine() with
        {
            Staircase = new[]
            {
                new StaircasePoint(32 * 1024, 1.05),
                new StaircasePoint(2 * 1024 * 1024, 5.83),
                new StaircasePoint(32 * 1024 * 1024, 56.68),
                new StaircasePoint(128 * 1024 * 1024, 40.00),   // -29 %, et 16,7 ns
            },
        };

        var verdict = MemoryQuality.Check(vraiRecul, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-02");
    }

    [Fact]
    public void Sans_frequence_lisible_Q01_se_TAIT_au_lieu_de_tout_rejeter()
    {
        // Q-01 compare le debit a un plafond. Quand la frequence n'a pas pu etre lue, ce
        // plafond vaut 0 -- et « debit > 0 » est toujours vrai, donc toute mesure etait rejetee,
        // apres avoir paye les trente secondes de banc. Le client lisait alors « la mesure n'a
        // pas pu etre menee a bien (Q-01) », pour une machine parfaitement saine dont on n'avait
        // simplement pas su lire la vitesse. Un controle sans reference ne conclut pas : il se tait.
        MemoryQuality.Check(Saine(), theoreticalPeakGBs: 0).Accepted.Should().BeTrue();
    }

    [Fact]
    public void Un_escalier_PLAT_est_rejete_car_l_instrument_n_a_vu_aucune_hierarchie()
    {
        // Le trou que q-05 FERME. Une sonde dont la boucle de fenetre serait cassee rendrait la
        // meme latence partout : au-dessus du plancher DRAM (donc Q-04 passe), jamais decroissante
        // (donc Q-02 passe), peu dispersee (donc Q-03 passe). La mesure etait ACCEPTEE, stockee
        // comme reference — et depuis qu'elle s'affiche, publiee.
        var plate = Saine() with
        {
            Staircase = new[]
            {
                new StaircasePoint(16 * 1024, 66.0),
                new StaircasePoint(512 * 1024, 66.0),
                new StaircasePoint(8 * 1024 * 1024, 66.0),
                new StaircasePoint(128 * 1024 * 1024, 66.0),
            },
        };

        var verdict = MemoryQuality.Check(plate, theoreticalPeakGBs: 60.8);

        verdict.Accepted.Should().BeFalse();
        verdict.RejectionId.Should().Be("Q-05", "un rejet doit designer CE qui a echoue");
    }

    [Fact]
    public void Une_courbe_qui_monte_DOUCEMENT_mais_saute_au_moins_une_fois_reste_acceptee()
    {
        // Le contre-controle de Q-05 : il ne doit pas exiger une belle hierarchie a quatre etages.
        // Un seul saut suffit — celui qui prouve que la sonde change bien de fenetre.
        var molle = Saine() with
        {
            Staircase = new[]
            {
                new StaircasePoint(16 * 1024, 40.0),
                new StaircasePoint(512 * 1024, 41.0),
                new StaircasePoint(8 * 1024 * 1024, 42.0),
                new StaircasePoint(128 * 1024 * 1024, 66.0),
            },
        };

        MemoryQuality.Check(molle, theoreticalPeakGBs: 60.8).Accepted.Should().BeTrue();
    }
}
