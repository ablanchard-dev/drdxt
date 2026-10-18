using DrDxt.Core;
using DrDxt.Detection.Diffusion;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Diffusion;

// Les journaux et réglages d'OBS, lus au format du code d'OBS 32.
public sealed class ObsStudioTests
{
    private static readonly DateTime T = new(2026, 10, 1, 21, 0, 0);

    // Lignes au format exact de libobs (obs-output.c, media-io/video-io.c), nom de sortie inventé.
    private const string Journal32 = """
        21:00:00.000: Output 'sortie-test': stopping
        21:00:00.000: Output 'sortie-test': Total frames output: 131904
        21:00:00.000: Output 'sortie-test': Total drawn frames: 121487 (131938 attempted)
        21:00:00.000: Output 'sortie-test': Number of lagged frames due to rendering lag/stalls: 10451 (7.9%)
        21:00:00.000: Video stopped, number of skipped frames due to encoding lag: 23210/131924 (17.6%)
        """;

    // Les lignes de bilan d'OBS 32 se lisent : un format inconnu ne doit pas devenir « ? ».
    [Fact]
    public void Le_bilan_d_obs_32_se_lit()
    {
        var s = ObsStudio.Bilan(Journal32, T)!;
        s.Reconnu.Should().BeTrue();
        s.Encodage.Should().BeApproximately(17.6, 0.05);
        s.Rendu.Should().BeApproximately(7.9, 0.05);
        s.Reseau.Should().Be(0);
    }

    // Une sortie lancée puis coupée (29 images manquées sur 42) ne fait pas un lancement perdu à 69 % : pondéré par les images.
    [Fact]
    public void Les_pertes_sont_ponderees_par_les_images()
    {
        const string deux = Journal32 + """

            22:00:00.000: Output 'courte': stopping
            22:00:00.000: Output 'courte': Total frames output: 40
            22:00:00.000: Output 'courte': Total drawn frames: 13 (42 attempted)
            22:00:00.000: Output 'courte': Number of lagged frames due to rendering lag/stalls: 29 (69.0%)
            """;
        ObsStudio.Bilan(deux, T)!.Rendu.Should().BeApproximately(100.0 * (10451 + 29) / (131938 + 42), 0.01);
    }

    [Fact]
    public void Un_obs_ouvert_sans_diffuser_n_est_pas_une_session()
        => ObsStudio.Bilan("21:00:00.000: OBS 32.2.2 (64-bit, windows)", T).Should().BeNull();

    // Une ligne non reconnue donne « non lu », jamais « 0 perte ».
    [Fact]
    public void Un_bilan_non_reconnu_reste_non_lu()
    {
        var s = ObsStudio.Bilan("21:00:00.000: Output 'x': stopping\n21:00:00.000: Output 'x': un autre format", T)!;
        s.Reconnu.Should().BeFalse();
        var pertes = ObsStudio.Juger(true, null, [s]).Single(c => c.Nom == "pertes en stream");
        pertes.Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Au_dessus_de_5_pourcent_le_pire_lancement_donne_un_constat_avec_sa_cause()
    {
        var c = ObsStudio.Juger(true, null, [ObsStudio.Bilan(Journal32, T)!]).Single(c => c.Nom == "pertes en stream");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().MatchRegex("17[.,]6");
        // Le nom d'une sortie peut porter l'identifiant d'un stream : il ne sort jamais.
        c.Explication.Should().NotContain("sortie-test");
        c.ValeurCourante.Should().NotContain("sortie-test");
    }

    [Fact]
    public void Les_seuils_sont_ceux_d_obs()
    {
        static RepairStatus Statut(double p) => ObsStudio.Juger(true, null, [new ObsStudio.Session(T, p, null, null, true)])
            .Single(c => c.Nom == "pertes en stream").Statut;
        Statut(0.2).Should().Be(RepairStatus.ExpectedDefault);
        Statut(3).Should().Be(RepairStatus.Observed);
        Statut(9.6).Should().Be(RepairStatus.Suspicious);
    }

    [Fact]
    public void Les_reglages_se_lisent_section_et_cle_exactes()
    {
        const string basic = "[Output]\r\nMode=Simple\r\n[SimpleOutput]\r\nVBitrate=6000\r\nStreamEncoder=x264\r\n[AdvOut]\r\nEncoder=obs_nvenc_h264_tex\r\n";
        ObsStudio.Ini(basic, "SimpleOutput", "StreamEncoder").Should().Be("x264");
        ObsStudio.Ini(basic, "AdvOut", "StreamEncoder").Should().BeNull();
        ObsStudio.EstLogiciel("x264").Should().BeTrue();
        ObsStudio.EstLogiciel("obs_nvenc_h264_tex").Should().BeFalse();
    }

    [Fact]
    public void Obs_absent_se_dit_et_rien_d_autre()
        => ObsStudio.Juger(false, null, null).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.ExpectedDefault);
}
