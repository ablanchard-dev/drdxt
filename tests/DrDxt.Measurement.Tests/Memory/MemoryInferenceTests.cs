using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class MemoryInferenceTests
{
    private static MemoryModule Barrette(int configuredMhz) =>
        new("KHX3600C17/8", "Kingston", ConfiguredMhz: configuredMhz, RatedMhz: 4000);

    // DDR : 8 octets par transfert (bus de 64 bits). 3800 MT/s => 30,4 Go/s par canal.
    [Fact]
    public void Le_pic_d_un_canal_se_calcule_de_la_frequence_configuree()
    {
        MemoryInference.PerChannelPeakGBs(new[] { Barrette(3800), Barrette(3800) })
            .Should().BeApproximately(30.4, 0.01);
    }

    [Fact]
    public void Depasser_le_pic_d_un_canal_PROUVE_qu_il_y_en_a_au_moins_deux()
    {
        // 34 Go/s releves sur la machine de reference le 06/09. Aucun canal unique a 3800 MT/s
        // ne peut rendre ca : son plafond theorique est 30,4. C'est une preuve, pas un indice --
        // et c'est ce que la lecture des chaines DeviceLocator ne peut pas donner quand la carte
        // nomme mal ses emplacements.
        MemoryInference.ProvenMinimumChannels(34.0, new[] { Barrette(3800), Barrette(3800) })
            .Should().Be(2);
    }

    [Fact]
    public void Un_debit_bas_ne_prouve_RIEN_car_un_seul_fil_ne_sature_pas_forcement_deux_canaux()
    {
        // Le piege que ce test ferme : conclure « simple canal » depuis un debit modeste. Le
        // facteur limitant peut etre le coeur, pas la memoire. La reponse honnete est 1 au sens
        // de « au moins un », donc rien de prouve.
        MemoryInference.ProvenMinimumChannels(19.0, new[] { Barrette(3800), Barrette(3800) })
            .Should().Be(1);
    }

    [Fact]
    public void Depasser_le_pic_de_deux_canaux_prouve_le_troisieme()
    {
        MemoryInference.ProvenMinimumChannels(65.0, new[] { Barrette(3800), Barrette(3800) })
            .Should().Be(3);
    }

    [Fact]
    public void Une_frequence_jamais_lue_ne_permet_aucune_conclusion()
    {
        var jamaisLue = new[] { Barrette(0), Barrette(0) };

        MemoryInference.PerChannelPeakGBs(jamaisLue).Should().Be(0);
        MemoryInference.ProvenMinimumChannels(34.0, jamaisLue).Should().Be(1);
    }

    [Fact]
    public void La_machine_tourne_a_la_vitesse_de_sa_barrette_la_plus_lente()
    {
        MemoryInference.PerChannelPeakGBs(new[] { Barrette(3800), Barrette(2133) })
            .Should().BeApproximately(17.064, 0.01);
    }

    [Fact]
    public void La_loi_de_Little_donne_les_lignes_en_vol_a_partir_du_debit_et_de_la_latence()
    {
        // 34 Go/s x 66 ns = 2244 octets en vol, soit 35 lignes de cache de 64 octets. Le nombre
        // est verifiable a la main, et c'est exprès : une valeur qu'on ne peut pas recalculer
        // soi-meme n'est pas un resultat, c'est une croyance.
        MemoryInference.LinesInFlight(bandwidthGBs: 34.0, latencyNs: 66.0)
            .Should().BeApproximately(35.06, 0.01);
    }

    [Fact]
    public void Le_plafond_a_donner_au_controle_Q01_majore_sans_supposer_la_topologie()
    {
        // Deux barrettes a 3800 : rien ne peut depasser 60,8 Go/s, quelle que soit la facon dont
        // elles sont cablees. C'est le nombre qui rend Q-01 vrai sans connaitre les canaux.
        MemoryInference.UpperBoundPeakGBs(new[] { Barrette(3800), Barrette(3800) })
            .Should().BeApproximately(60.8, 0.01);
    }
}
