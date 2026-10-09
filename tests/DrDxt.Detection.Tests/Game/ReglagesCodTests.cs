using DrDxt.Core;
using DrDxt.Detection.Game;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Game;

public class ReglagesCodTests
{
    // Extrait du fichier reel de la machine de reference (s.1.0.cod24.txt0, 14/06/2026).
    private const string Extrait = """
        // Enable Nvidia low latency mode. Boost mode request maximum GPU clock frequency regardless of workload
        NvidiaReflex@0;33761;11445 = Enabled
        PreferredDisplayMode@0;15994;11445 = Fullscreen borderless window // one of Fullscreen, Fullscreen borderless window
        VSync@0;55075;7764 = disabled
        CapFps@0;57752;20945 = true
        MaxFpsInGame@0;13305;58861 = 155 // 30 to 300
        DLSSFrameGeneration@0;57752;48403 = false
        """;

    private static readonly DateTime Recent = new(2026, 9, 20);

    [Fact]
    public void Le_fichier_de_la_machine_de_reference_se_lit_tel_que_le_jeu_l_ecrit()
    {
        var r = ReglagesCod.Lire("f", Extrait, Recent);
        r.Reflex.Should().Be("Enabled");
        r.VSync.Should().Be("disabled");
        r.CapFps.Should().BeTrue();
        r.MaxFpsEnJeu.Should().Be(155);
        r.ModeAffichage.Should().Be("Fullscreen borderless window");
        r.GenerationDImages.Should().BeFalse();
    }

    [Fact]
    public void Reflex_active_ne_recommande_rien_et_le_constat_dit_ce_qui_est_lu()
    {
        var c = ReglagesCod.Constats(ReglagesCod.Lire("f", Extrait, Recent), Recent);
        c.Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Observed);
        c[0].Explication.Should().Contain("155");
        // 25/09/2026 : la valeur du jeu est citée comme telle, avec sa source.
        c[0].ValeurCourante.Should().MatchRegex(@"^Reflex « .+ » \(fichier de réglages de Call of Duty\)$");
    }

    [Fact]
    public void Reflex_desactive_et_generation_d_images_sont_des_recommandations()
    {
        var texte = Extrait.Replace("= Enabled", "= Disabled").Replace("DLSSFrameGeneration@0;57752;48403 = false", "DLSSFrameGeneration@0;57752;48403 = true");
        var c = ReglagesCod.Constats(ReglagesCod.Lire("f", texte, Recent), Recent);
        c.Should().HaveCount(2).And.OnlyContain(x => x.Action == RepairAction.Recommandation);
    }

    [Fact]
    public void Un_fichier_vieux_de_plus_de_trente_jours_le_dit()
        => ReglagesCod.Constats(ReglagesCod.Lire("f", Extrait, new DateTime(2026, 6, 14)), Recent)[0]
            .Explication.Should().Contain("peut-être dépassées");

    [Fact]
    public void Un_fichier_sans_ces_cles_ne_produit_aucun_constat()
        => ReglagesCod.Constats(ReglagesCod.Lire("f", "// rien\nFoo@1 = bar", Recent), Recent).Should().BeEmpty();

    [Fact]
    public void Le_jeu_alterne_txt0_et_txt1_et_c_est_le_plus_recent_qui_compte()
    {
        var d = Directory.CreateTempSubdirectory("drdxt-cod-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(d, "s.1.0.cod25.txt0"), "ancien");
            File.WriteAllText(Path.Combine(d, "s.1.0.cod25.txt1"), "recent");
            File.WriteAllText(Path.Combine(d, "s.1.0.cod25.m"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(d, "s.1.0.cod25.txt0"), new DateTime(2026, 9, 15));
            File.SetLastWriteTimeUtc(Path.Combine(d, "s.1.0.cod25.txt1"), new DateTime(2026, 9, 16));
            ReglagesCod.PlusRecent(new[] { d, Path.Combine(d, "absent") })!.Name.Should().Be("s.1.0.cod25.txt1");
        }
        finally { Directory.Delete(d, true); }
    }

    // 29/09/2026 : en partie, la carte de la machine de référence tourne à 97 % ; ses FPS se jouent dans la carte, pas
    // dans Windows. Le levier logiciel qui reste est le mode DLSS du jeu. Extrait du fichier réel (s.1.0.cod25.txt1, 25/09).
    private const string ExtraitDlss = """
        NvidiaReflex@0;33761;11445 = Enabled + boost
        DLSSModeMP@0;43179;20945 = DLSS
        DLSSPerfModeMP@0;27441;8284 = Balanced
        """;

    [Fact]
    public void DLSS_equilibre_dit_ce_que_le_mode_performance_retire_sans_promettre_de_FPS()
    {
        var r = ReglagesCod.Lire("f", ExtraitDlss, Recent);
        r.ModeDlss.Should().Be("Balanced");

        var dlss = ReglagesCod.Constats(r, Recent).Single(x => x.Nom == "mode DLSS de Call of Duty");
        dlss.ValeurCourante.Should().Be("Balanced");
        dlss.Explication.Should().Contain("58 %").And.Contain("50 %").And.Contain("26 %")
            .And.Contain("pas mesuré").And.Contain("NVIDIA");
    }

    [Theory]
    [InlineData("Performance")]
    [InlineData("Ultra Performance")]
    public void DLSS_deja_en_performance_ne_dit_rien(string mode)
        => ReglagesCod.Constats(ReglagesCod.Lire("f", ExtraitDlss.Replace("= Balanced", "= " + mode), Recent), Recent)
            .Should().NotContain(x => x.Nom == "mode DLSS de Call of Duty");

    [Fact]
    public void Sans_DLSS_comme_technique_le_mode_DLSS_ne_dit_rien()
        => ReglagesCod.Constats(ReglagesCod.Lire("f", ExtraitDlss.Replace("= DLSS\n", "= FSR\n").Replace("= DLSS\r\n", "= FSR\r\n"), Recent), Recent)
            .Should().NotContain(x => x.Nom == "mode DLSS de Call of Duty");

    [Fact]
    public void Reflex_avec_boost_n_est_pas_desactive()
        => ReglagesCod.Constats(ReglagesCod.Lire("f", Extrait.Replace("= Enabled", "= Enabled + boost // one of Disabled"), Recent), Recent)
            .Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Observed);
}
