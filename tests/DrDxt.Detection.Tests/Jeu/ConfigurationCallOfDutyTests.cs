using DrDxt.Core;
using DrDxt.Detection.Jeu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Jeu;

// D2 (10/10/2026) : les fils de rendu de Call of Duty, lus (jamais écrits) dans son fichier de réglages, comparés aux
// cœurs performants estimés. Forme relevée sur la machine de référence.
public sealed class ConfigurationCallOfDutyTests
{
    private const string Fichier = """
        VideoMemoryScaleMP@0;59710;7707 = 0.700000 // 0.000000 to 2.000000
        RendererWorkerCount@0;51989;59387 = 6 // -1 to 16
        """;

    [Fact]
    public void La_ligne_relevee_sur_la_machine_de_reference_se_lit()
    {
        ConfigurationCallOfDuty.FilsDeRendu(Fichier).Should().Be(6);
        ConfigurationCallOfDuty.FilsDeRendu("RendererWorkerCount = -1").Should().Be(-1);
        ConfigurationCallOfDuty.FilsDeRendu("VideoMemoryScaleMP@0 = 0.7").Should().BeNull();
        ConfigurationCallOfDuty.FilsDeRendu(null).Should().BeNull();
    }

    [Theory]
    [InlineData("GenuineIntel", 20, 28, 8)]   // i7-14700F : 8 P + 12 E
    [InlineData("GenuineIntel", 6, 12, 6)]    // i5-12400F : pas d'E-cores
    [InlineData("AuthenticAMD", 6, 12, 6)]    // Ryzen 5 5600
    [InlineData("GenuineIntel", 14, 20, 6)]   // i5-13600K : 6 P + 8 E
    public void Les_coeurs_performants_s_estiment_sur_l_architecture(string vendeur, int coeurs, int fils, int attendu)
        => ConfigurationCallOfDuty.CoeursPerformants(vendeur, coeurs, fils).Should().Be(attendu);

    [Fact]
    public void Dans_la_plage_c_est_une_information_loin_c_est_a_regarder_avec_le_guide()
    {
        var ok = ConfigurationCallOfDuty.Juger((6, "s.1.0.cod24.txt0"), 8).Should().ContainSingle().Subject;
        ok.Statut.Should().Be(RepairStatus.Observed);
        ok.Action.Should().Be(RepairAction.Aucune);
        ok.Explication.Should().Contain("8 cœurs performants").And.Contain("5.7").And.NotMatchRegex(@"\d+\s*%");

        var loin = ConfigurationCallOfDuty.Juger((2, "s.1.0.cod24.txt0"), 8).Should().ContainSingle().Subject;
        loin.Statut.Should().Be(RepairStatus.Suspicious);
        loin.Action.Should().Be(RepairAction.Recommandation);
        loin.ValeurAttendue.Should().Contain("6").And.Contain("8");
    }

    [Fact]
    public void Automatique_est_dit_tel_quel_et_sans_fichier_rien()
    {
        var auto = ConfigurationCallOfDuty.Juger((-1, "s.1.0.cod24.txt0"), 8).Should().ContainSingle().Subject;
        auto.ValeurCourante.Should().Contain("automatique");
        auto.Action.Should().Be(RepairAction.Aucune);
        ConfigurationCallOfDuty.Juger(null, 8).Should().BeEmpty();
    }

    [Fact]
    public void Le_dossier_des_joueurs_se_lit_fichier_le_plus_recent_d_abord()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-cod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "s.1.0.cod24.txt0"), "RendererWorkerCount@0;1;2 = 4 // -1 to 16");
            File.WriteAllText(Path.Combine(dossier, "s.1.0.cod24.m"), "0");
            ConfigurationCallOfDuty.Lire(dossier).Should().Be((4, "s.1.0.cod24.txt0"));
            ConfigurationCallOfDuty.Lire(Path.Combine(dossier, "absent")).Should().BeNull();
        }
        finally { Directory.Delete(dossier, true); }
    }
}
