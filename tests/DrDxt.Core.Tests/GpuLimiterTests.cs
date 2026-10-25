using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Ce qui a ete observe, et rien de plus.
///
/// La frequence GPU et la raison de bridage ne demandent pas NVAPI : `nvmlDeviceGetClockInfo` et
/// `nvmlDeviceGetCurrentClocksEventReasons` sont des exports NVML publics et documentes (source :
/// documentation officielle). Releve sur une RTX 4060 le 08/09/2026 : 2490 MHz, 55,8 W, 58 °C, masque 0x1.
///
/// Ce classifieur ne rend jamais une cause. « un plafond de puissance a ete observe pendant les
/// fenetres mesurees » est une coincidence temporelle ; l'attribuer a une perte de FPS demanderait
/// de changer la limite et de remesurer, ce qui est une experience, pas une lecture.
/// </summary>
public class GpuLimiterTests
{
    [Fact]
    public void ZERO_RAISON_LUE_n_est_PAS_la_meme_chose_que_RIEN_DE_LU()
    {
        // Le piege propre a cette API : `nvmlClocksEventReasonNone` VAUT 0x0. Un code qui
        // teste « masque != 0 » pour savoir si la lecture a marche effacerait exactement
        // l'information la plus rassurante — « rien ne bride » deviendrait « on ne sait pas ».
        GpuLimiter.Classer(ClocksEventReason.Aucune, null, null, null)
            .Should().Be(GpuLimiterObserved.RienDeBridant);

        GpuLimiter.Classer(null, null, null, null)
            .Should().Be(GpuLimiterObserved.Inconnu);
    }

    // Sur une carte AMD ou un NVML muet, seule l'occupation se lit : « rien ne bridait le GPU » s'affirmait sans aucune raison
    // ni fréquence lue. Il faut l'une ou l'autre ; une fréquence lue et stable suffit.
    [Fact]
    public void L_occupation_seule_ne_dit_pas_que_rien_ne_bride()
    {
        GpuLimiter.Classer(null, 0.70, null, null).Should().Be(GpuLimiterObserved.Inconnu);
        GpuLimiter.Classer(null, 0.70, 2400, 2380).Should().Be(GpuLimiterObserved.RienDeBridant);
    }

    [Fact]
    public void UN_RALENTISSEMENT_THERMIQUE_PUBLIE_est_nomme_tel_quel()
    {
        GpuLimiter.Classer(ClocksEventReason.ThermiqueMateriel, 0.99, 2400, 2350)
            .Should().Be(GpuLimiterObserved.PlafondThermiqueObserve);

        GpuLimiter.Classer(ClocksEventReason.ThermiqueLogiciel, null, null, null)
            .Should().Be(GpuLimiterObserved.PlafondThermiqueObserve);
    }

    [Fact]
    public void UN_PLAFOND_DE_PUISSANCE_PUBLIE_bat_l_occupation_GPU()
    {
        // Une raison publiee par le pilote bat toute inference de notre part : lui sait, nous
        // devinons. Meme a 99 % d'occupation, si le pilote dit « plafond de puissance », c'est
        // ca qu'on affiche.
        GpuLimiter.Classer(ClocksEventReason.PlafondPuissanceLogiciel, 0.99, 2400, 2400)
            .Should().Be(GpuLimiterObserved.PlafondPuissanceObserve);
    }

    [Fact]
    public void UNE_CHUTE_DE_FREQUENCE_SANS_RAISON_PUBLIEE_est_dite_SANS_explication()
    {
        // Un fait sans explication vaut mieux qu'une explication inventee.
        GpuLimiter.Classer(ClocksEventReason.Aucune, 0.99, 2500, 2000)
            .Should().Be(GpuLimiterObserved.ChuteDeFrequenceObservee);

        GpuLimiter.Phrase(GpuLimiterObserved.ChuteDeFrequenceObservee)
            .Should().Contain("sans raison publiée");
    }

    [Fact]
    public void UN_BOOST_ORDINAIRE_NE_DECLENCHE_RIEN()
    {
        // Le controle negatif : une carte NVIDIA monte et descend en permanence de quelques
        // pourcents. Un seuil trop serre transformerait ce comportement normal en alerte.
        GpuLimiter.Classer(ClocksEventReason.Aucune, 0.99, 2500, 2400)
            .Should().Be(GpuLimiterObserved.GpuSature, "5 % de variation est le boost normal");
    }

    [Fact]
    public void UN_GPU_OCCUPE_SANS_AUCUN_PLAFOND_est_dit_SATURE_pas_BRIDE()
    {
        // La nuance qui compte : « le GPU etait occupe » n'est pas « le GPU etait bride ». La
        // premiere phrase decrit une scene GPU-bound ; la seconde accuserait un plafond.
        var o = GpuLimiter.Classer(ClocksEventReason.Aucune, 0.97, 2490, 2490);

        o.Should().Be(GpuLimiterObserved.GpuSature);
        GpuLimiter.Phrase(o).Should().Contain("occupé").And.NotContain("plafond");
    }

    [Fact]
    public void AUCUNE_PHRASE_NE_PRETEND_A_UNE_CAUSE()
    {
        // La garde de vocabulaire. Ce produit a deja qualifie une preuve plus fortement que ce
        // qu'elle prouvait ; ces phrases sont lues par le joueur et doivent rester des
        // OBSERVATIONS.
        foreach (var o in Enum.GetValues<GpuLimiterObserved>())
        {
            var phrase = GpuLimiter.Phrase(o);
            phrase.Should().NotContainAny("cause", "a cause de", "explique", "responsable",
                "parce que", "te coute");
        }
    }
}
