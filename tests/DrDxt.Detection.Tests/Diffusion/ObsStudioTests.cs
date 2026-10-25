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

    // Une sortie arrêtée sur « Total frames output: 0 » : bilan reconnu, mais aucune image comptée. Rien n'est mesuré,
    // donc jamais « pertes à 1 % ou moins ».
    [Fact]
    public void Un_lancement_sans_aucune_image_comptee_n_est_pas_dit_propre()
    {
        var s = ObsStudio.Bilan(Arret + "21:00:00.000: Output 'x': Total frames output: 0\n", T)!;
        ObsStudio.Juger(true, null, [s]).Single(c => c.Nom == "pertes en stream").Statut.Should().Be(RepairStatus.Unknown);
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

    private const string Arret = "21:00:00.000: Output 'x': stopping\n";

    // Un journal abîmé ne fait pas tomber la famille : la ligne illisible est sautée, le bilan reste « non reconnu ».
    [Theory]
    [InlineData("21:00:00.000: Video stopped, number of skipped frames due to encoding lag: ٣/٥ (1.0%)")]
    [InlineData("21:00:00.000: Video stopped, number of skipped frames due to encoding lag: 99999999999999999999/5 (1.0%)")]
    public void Un_nombre_illisible_saute_la_ligne_sans_lever(string ligne)
    {
        var s = ObsStudio.Bilan(Arret + ligne, T)!;

        s.Reconnu.Should().BeFalse();
        s.Encodage.Should().BeNull();
    }

    // Plus d'images sautées que d'images tentées n'est pas un bilan : jamais « 99900 % ».
    [Fact]
    public void Plus_de_pertes_que_d_images_n_est_pas_un_bilan()
    {
        var s = ObsStudio.Bilan(Arret + "21:00:00.000: Video stopped, number of skipped frames due to encoding lag: 999/1 (99900.0%)", T)!;

        s.Encodage.Should().BeNull();
    }

    private static string Profil(string user, string? streamEncoder = null)
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"obs-{Guid.NewGuid():N}");
        var profil = Path.Combine(dossier, "basic", "profiles", "P");
        Directory.CreateDirectory(profil);
        File.WriteAllText(Path.Combine(dossier, "user.ini"), user);
        File.WriteAllText(Path.Combine(profil, "basic.ini"), "[Output]\nMode=Advanced\n[AdvOut]\nEncoder=obs_nvenc_h264_tex\n");
        if (streamEncoder is not null) File.WriteAllText(Path.Combine(profil, "streamEncoder.json"), streamEncoder);
        return dossier;
    }

    // Un streamEncoder.json dont la racine n'est pas un objet : débit non lu, le reste des réglages tient.
    [Theory]
    [InlineData("[1]")]
    [InlineData("1")]
    public void Un_encodeur_json_qui_n_est_pas_un_objet_laisse_le_debit_non_lu(string json)
    {
        var dossier = Profil("[Basic]\nProfileDir=P\n", json);
        try
        {
            var r = ObsStudio.LireReglages(dossier)!;

            r.Encodeur.Should().Be("obs_nvenc_h264_tex");
            r.DebitKbps.Should().BeNull();
        }
        finally { Directory.Delete(dossier, true); }
    }

    // ProfileDir nomme un profil sous le dossier d'OBS : un chemin absolu ou réseau n'est jamais ouvert.
    [Fact]
    public void Un_profil_absolu_n_est_pas_lu()
    {
        var ailleurs = Profil("[Basic]\nProfileDir=P\n");
        var dossier = Profil($"[Basic]\nProfileDir={Path.Combine(ailleurs, "basic", "profiles", "P")}\n");
        try
        {
            ObsStudio.LireReglages(dossier).Should().BeNull();
        }
        finally { Directory.Delete(dossier, true); Directory.Delete(ailleurs, true); }
    }

    // Une cause qu'aucun lancement n'a comptée (pas de ligne de bilan pour elle) se dit non comptée, jamais « 0 % ».
    [Fact]
    public void Une_cause_jamais_comptee_ne_vaut_pas_zero()
    {
        var s = new ObsStudio.Session(T, Encodage: null, Rendu: 2.0, Reseau: 0.0, Reconnu: true);

        var c = ObsStudio.Juger(true, null, [s]).Single(c => c.Nom == "pertes en stream");

        c.ValeurCourante.Should().Be(Docteur.T("OBS.ValeurPertes", Docteur.T("OBS.NonCompte"), "2 %", "0 %"));
    }
}
