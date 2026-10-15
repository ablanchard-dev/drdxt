using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// « ce que DrDXT fait sur ton PC ». Chaque levier du catalogue a un statut sur la machine lue :
/// posé, à poser, déjà bon, non lu, ou pas sur ce matériel avec sa raison. Fin de la navigation à l'aveugle.
/// </summary>
public sealed class CouvertureDesLeviersTests
{
    private static readonly SettingsState Etat = new(TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Machine(ChassisType chassis = ChassisType.Desktop, string gpu = "NVIDIA GeForce RTX 4060",
        string vendeur = "NVIDIA", long vram = 8L << 30, bool x3d = false, int ccd = 1) => new(
        new CpuInfo(x3d ? "Ryzen 9 7950X3D" : "Core i7-14700F", x3d ? "AMD" : "Intel", 8, 16, x3d, ccd),
        new[] { new GpuInfo(gpu, vendeur, vram, "1", TriState.On) },
        new[] { new MemoryModule("PN", "K", 3200, 3200) }, new MotherboardInfo("M", "B"), new BiosInfo("1", null, true), chassis);

    private static ITweak Levier(string id, bool applique = false, bool nonLu = false)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(id);
        t.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(applique);
        t.EtatNonLu(Arg.Any<SettingsState>()).Returns(nonLu);
        return t;
    }

    private static LevierSurCeMateriel Un(ITweak t, HardwareInventory hw, params string[] poses) =>
        CouvertureDesLeviers.Calculer([t], hw, Etat, poses.ToHashSet()).Single();

    [Fact]
    public void Pose_l_emporte_sur_tout() =>
        Un(Levier("latency.gameModeOn", applique: true), Machine(), "latency.gameModeOn").Statut.Should().Be(StatutDuLevier.Pose);

    [Fact]
    public void Applicable_est_a_poser() =>
        Un(Levier("latency.gameModeOn", applique: true), Machine()).Statut.Should().Be(StatutDuLevier.APoser);

    [Fact]
    public void Le_decalage_de_la_carte_vit_dans_le_mode_Expert_jamais_a_poser() =>
        Un(Levier("gpu.undervolt", applique: true), Machine()).Statut.Should().Be(StatutDuLevier.Expert);

    [Fact]
    public void Non_lu_est_dit() =>
        Un(Levier("latency.hagsOn", nonLu: true), Machine()).Statut.Should().Be(StatutDuLevier.NonLu);

    [Fact]
    public void Ni_applicable_ni_non_lu_sur_le_bon_materiel_est_deja_bon() =>
        Un(Levier("latency.gameModeOn"), Machine()).Statut.Should().Be(StatutDuLevier.DejaBon);

    [Theory]
    [InlineData("fixit.power_plan", ChassisType.Laptop, "Levier.PasIci.Batterie")]
    [InlineData("latency.prefer_max_perf", ChassisType.Laptop, "Levier.PasIci.Batterie")]
    [InlineData("gpu.power_limit", ChassisType.Laptop, "Levier.PasIci.FrequencesVerrouillees")]
    [InlineData("gpu.undervolt", ChassisType.Laptop, "Levier.PasIci.FrequencesVerrouillees")]
    [InlineData("fixit.mode_boost", ChassisType.Handheld, "Levier.PasIci.Console")]
    [InlineData("latency.render_queue_cap", ChassisType.Handheld, "Levier.PasIci.Console")]
    public void Le_chassis_donne_sa_raison(string id, ChassisType chassis, string raison)
    {
        var l = Un(Levier(id), Machine(chassis));
        l.Statut.Should().Be(StatutDuLevier.PasIci);
        l.RaisonCle.Should().Be(raison);
    }

    [Fact]
    public void Sans_NVIDIA_les_reglages_du_pilote_NVIDIA_ne_s_appliquent_pas() =>
        Un(Levier("latency.prefer_max_perf"), Machine(gpu: "AMD Radeon RX 7800 XT", vendeur: "AMD")).RaisonCle
            .Should().Be("Levier.PasIci.PasNvidia");

    [Fact]
    public void Sans_carte_dediee_les_reglages_de_carte_ne_s_appliquent_pas() =>
        Un(Levier("gpu.power_limit"), Machine(gpu: "Intel UHD 770", vendeur: "Intel", vram: 0)).RaisonCle
            .Should().Be("Levier.PasIci.PasDeCarteDediee");

    [Fact]
    public void Le_routage_X3D_ne_concerne_qu_un_X3D_a_deux_blocs()
    {
        Un(Levier("fixit.x3d_ccd"), Machine()).RaisonCle.Should().Be("Levier.PasIci.PasX3d");
        // La raison inverse, pour le plan d'alimentation.
        var x3d = Machine(); x3d = x3d with { Cpu = x3d.Cpu with { IsX3D = true, CcdCount = 2 } };
        Un(Levier("fixit.power_plan"), x3d).RaisonCle.Should().Be("Levier.PasIci.X3dDeuxBlocs");
        Un(Levier("fixit.x3d_ccd"), Machine(x3d: true, ccd: 2)).Statut.Should().Be(StatutDuLevier.DejaBon);
    }

    [Fact]
    public void Reflex_dit_pourquoi_il_ne_se_propose_jamais()
    {
        Un(Levier("latency.reflex"), Machine()).RaisonCle.Should().Be("Levier.PasIci.ReflexDuJeu");
        Un(Levier("latency.reflex"), Machine()).Statut.Should().Be(StatutDuLevier.SansObjet, "ce n'est pas une question de matériel");
    }

    // Le ventilateur NVIDIA est construit, en mode Expert sur un PC fixe ; ailleurs, la raison.
    [Fact]
    public void Le_ventilateur_est_Expert_sur_un_fixe_NVIDIA_et_dit_pourquoi_ailleurs()
    {
        Un(Levier("gpu.fan_curve"), Machine()).Statut.Should().Be(StatutDuLevier.Expert);
        Un(Levier("gpu.fan_curve"), Machine(chassis: ChassisType.Laptop)).RaisonCle.Should().Be("Levier.PasIci.VentilateurConstructeur");
        Un(Levier("gpu.fan_curve"), Machine(gpu: "Radeon RX 7800 XT", vendeur: "AMD")).RaisonCle.Should().Be("Levier.PasIci.PasNvidia");
    }

    [Fact]
    public void Chaque_levier_a_un_domaine_de_page()
    {
        foreach (var id in new[] { "latency.gameModeOn", "gpu.power_limit", "fixit.power_plan", "fixit.sane_pagefile", "inconnu" })
            CouvertureDesLeviers.Domaine(id).Should().NotBeNullOrEmpty();
    }

    // « vérifié ici » vient de « Vérifier DrDXT » sur cette machine ; jamais sur un levier qui n'est pas
    // pour ce matériel.
    [Fact]
    public void Un_levier_verifie_ici_le_dit_sauf_s_il_n_est_pas_pour_ce_materiel()
    {
        var verifies = new HashSet<string> { "latency.gameModeOn", "fixit.x3d_ccd", "gpu.fan_curve" };
        var liste = CouvertureDesLeviers.Calculer([Levier("latency.gameModeOn"), Levier("fixit.x3d_ccd"), Levier("gpu.fan_curve")], Machine(), Etat,
            new HashSet<string>(), verifies);
        liste.Single(l => l.Id == "latency.gameModeOn").VerifieIci.Should().BeTrue();
        liste.Single(l => l.Id == "gpu.fan_curve").VerifieIci.Should().BeFalse("Expert : la vérification ne le pose pas");
        liste.Single(l => l.Id == "fixit.x3d_ccd").VerifieIci.Should().BeFalse("pas un X3D : pas pour ce matériel");
    }
}
