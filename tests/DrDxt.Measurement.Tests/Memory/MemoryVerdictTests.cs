using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class MemoryVerdictTests
{
    private const double PicTheorique608 = 60.8;

    private static MemoryMeasurement Mesure(double bandwidthGBs, double latencyNs) => new(
        BandwidthGBs: bandwidthGBs,
        BandwidthSigmaPct: 1.83,
        LatencyNs: latencyNs,
        LatencySigmaPct: 3.45,
        Staircase: new[]
        {
            new StaircasePoint(16 * 1024, 1.2),
            new StaircasePoint(128 * 1024 * 1024, latencyNs),
        });

    [Fact]
    public void Le_passage_de_XMP_off_a_XMP_on_est_un_resultat_car_il_ecrase_le_bruit()
    {
        // 2133 -> 3800 MT/s : le debit monte d'environ 78 %. Devant 3 sigma (moins de 8 %),
        // ce n'est pas une question de seuil, c'est un autre regime.
        var avant = Mesure(bandwidthGBs: 19.1, latencyNs: 88.0);
        var apres = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0);

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.HasVerdict.Should().BeTrue();
        verdict.BandwidthSignificant.Should().BeTrue();
        verdict.BandwidthDeltaPct.Should().BeApproximately(78.0, 1.0);
    }

    [Fact]
    public void Un_gain_de_2_pourcent_n_est_PAS_un_resultat_car_il_tient_sous_le_bruit()
    {
        // Le test qui protege le client. 3 sigma valent ici 7,8 % sur le debit : 2 % est
        // exactement la taille d'ecart qu'un instrument produit sans que rien n'ait change.
        var avant = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0);
        var apres = Mesure(bandwidthGBs: 34.68, latencyNs: 66.0);

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.HasVerdict.Should().BeTrue();
        verdict.BandwidthSignificant.Should().BeFalse();
    }

    [Fact]
    public void Une_latence_qui_baisse_de_25_pourcent_est_un_resultat_et_son_delta_est_negatif()
    {
        var avant = Mesure(bandwidthGBs: 34.0, latencyNs: 88.0);
        var apres = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0);

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.LatencySignificant.Should().BeTrue();
        verdict.LatencyDeltaPct.Should().BeApproximately(-25.0, 0.5);
    }

    [Fact]
    public void Une_mesure_rejetee_ne_rend_AUCUN_verdict_et_le_rapport_dit_de_quel_cote()
    {
        // « On ne se prononce pas » n'est pas « pas de gain ». Un A/B dont une moitie est fausse
        // doit se taire, et dire ou.
        var avant = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0);
        var apresAgitee = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0) with { LatencySigmaPct = 12.0 };

        var verdict = MemoryVerdict.Compare(avant, apresAgitee, PicTheorique608);

        verdict.HasVerdict.Should().BeFalse();
        verdict.NoVerdictReason.Should().Be("Q-03 (apres)");
        verdict.BandwidthSignificant.Should().BeFalse();
    }

    [Fact]
    public void Un_ecart_sous_le_bruit_rend_la_RESOLUTION_et_pas_seulement_un_non()
    {
        // 17/09/2026 — mesure sur la machine de reference : la memoire tourne a 3 800 MT/s pour un kit
        // annonce a 4 000, soit +5,3 % de debit theorique si le profil du kit etait applique.
        // L'instrument repond « pas significatif ». Sans sa resolution, le lecteur entend
        // « monter a 4 000 ne sert a rien » ; la phrase vraie est « je ne sais pas distinguer
        // moins de 7,76 % ». C'est deja ce que le tableau de bord affiche pour le GPU
        // (Scan.Drift.None cite sa resolution) : la memoire etait la seule a ne pas le dire.
        //
        // 7,76 % vient du bareme, pas du code : 3 x racine(1,83^2 + 1,83^2).
        var avant = Mesure(bandwidthGBs: 35.89, latencyNs: 69.49);
        var apres = Mesure(bandwidthGBs: 35.89 * 1.053, latencyNs: 69.49);

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.BandwidthSignificant.Should().BeFalse();
        verdict.BandwidthResolutionPct.Should().BeApproximately(7.76, 0.02);
        verdict.LatencyResolutionPct.Should().BeApproximately(14.64, 0.02);
    }

    // 26/09/2026 : une mutation qui ne comptait que les HAUSSES de debit restait verte. Le cas qu'elle
    // cache est celui qu'on veut voir : une mise a jour du BIOS qui remet la memoire a 2133 MT/s.
    [Fact]
    public void Un_debit_qui_s_effondre_est_un_resultat_et_son_delta_est_negatif()
    {
        var avant = Mesure(bandwidthGBs: 34.0, latencyNs: 66.0);
        var apres = Mesure(bandwidthGBs: 19.1, latencyNs: 66.0);

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.BandwidthSignificant.Should().BeTrue();
        verdict.BandwidthDeltaPct.Should().BeApproximately(-43.8, 0.1);
    }

    [Fact]
    public void Un_ecart_egal_a_trois_sigma_ne_suffit_pas_il_faut_le_depasser()
    {
        // Sigma 3 et 4 : 3 x racine(9 + 16) = 15 exactement, et 40 -> 46 donne exactement +15 %.
        var avant = Mesure(bandwidthGBs: 40.0, latencyNs: 60.0) with { BandwidthSigmaPct = 3.0, LatencySigmaPct = 3.0 };
        var apres = Mesure(bandwidthGBs: 46.0, latencyNs: 69.0) with { BandwidthSigmaPct = 4.0, LatencySigmaPct = 4.0 };

        var verdict = MemoryVerdict.Compare(avant, apres, PicTheorique608);

        verdict.HasVerdict.Should().BeTrue();
        verdict.BandwidthDeltaPct.Should().Be(15.0);
        verdict.LatencyDeltaPct.Should().Be(15.0);
        verdict.BandwidthSignificant.Should().BeFalse();
        verdict.LatencySignificant.Should().BeFalse();
    }

    [Fact]
    public void Un_debit_avant_nul_ne_fabrique_pas_un_gain_infini()
    {
        // MemoryQuality ne rejette pas un debit nul. Sans le garde, 0 -> 34 donnait +infini %, « significatif ».
        var verdict = MemoryVerdict.Compare(Mesure(0.0, 66.0), Mesure(34.0, 66.0), PicTheorique608);

        verdict.BandwidthDeltaPct.Should().Be(0);
        verdict.BandwidthSignificant.Should().BeFalse();
    }
}
