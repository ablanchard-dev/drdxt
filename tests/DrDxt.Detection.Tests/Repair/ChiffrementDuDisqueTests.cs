using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le disque des jeux chiffré : protection suspendue et conversion en cours se disent ; déchiffrer n'est jamais proposé.
public sealed class ChiffrementDuDisqueTests
{
    [Fact]
    public void Une_protection_suspendue_et_une_conversion_en_cours_meritent_le_regard()
    {
        ChiffrementDuDisque.Juger([new("C", 0, 1, 7, 100)], ["C:"]).Single().ValeurCourante.Should().Be("protection suspendue");
        var conversion = ChiffrementDuDisque.Juger([new("D", 1, 2, 7, 42)], ["D"]).Single();
        conversion.ValeurCourante.Should().Be("conversion en cours (42 %)");
        ChiffrementDuDisque.ConversionEnCours([new("D", 1, 2, 7, 42)]).Should().BeTrue();
        ChiffrementDuDisque.Juger([new("C", 1, 1, 5, 100)], ["C"]).Single().Statut.Should().Be(RepairStatus.Observed);
        ChiffrementDuDisque.Juger([], ["C"]).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Sans_droits_c_est_non_lu_et_rien_ne_propose_de_dechiffrer()
    {
        ChiffrementDuDisque.Juger(null, ["C"]).Single().Statut.Should().Be(RepairStatus.Unknown);
        ChiffrementDuDisque.ConversionEnCours(null).Should().BeNull();
        foreach (var v in new ChiffrementDuDisque.Volume[] { new("C", 0, 1, 7, 100), new("C", 1, 3, 7, 10), new("C", 1, 1, 7, 100) })
            ChiffrementDuDisque.Juger([v], ["C"]).Single().Remede?.Should().NotContain("-off").And.NotContain("Désactiver");
    }

    [Fact]
    public void Une_conversion_en_pause_n_occupe_pas_le_disque_et_un_etat_non_lu_n_est_pas_une_suspension()
    {
        // GetConversionStatus 4 et 5 : en pause, rien ne tourne sur le disque.
        ChiffrementDuDisque.ConversionEnCours([new("D", 0, 4, 7, 30)]).Should().BeFalse();
        var pause = ChiffrementDuDisque.Juger([new("D", 0, 5, 7, 30)], ["D"]).Single();
        pause.Statut.Should().Be(RepairStatus.Observed);
        pause.ValeurCourante.Should().Be("conversion en pause (30 %)");
        // Conversion non lue : un volume en clair rend aussi ProtectionStatus 0, ce n'est pas une protection suspendue.
        ChiffrementDuDisque.Juger([new("C", 0, null, 0, null)], ["C"]).Single().Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Un_nom_d_instance_court_ne_fait_pas_perdre_toute_la_memoire_video()
        => MemoireVideoOccupee.ParPid([("ab", 9), ("pid_7_luid_0x0_0x1_phys_0", 300)]).Should().Equal((7, 300L));

    [Fact]
    public void La_chaine_audio_virtuelle_et_la_memoire_video_par_processus_se_lisent()
    {
        ChaineAudio.Filtrer(["Realtek(R) Audio", "VB-Audio Hi-Fi Cable", "NVIDIA High Definition Audio"]).Should().Equal("VB-Audio Hi-Fi Cable");
        MemoireVideoOccupee.ParPid([("pid_12_luid_0x0_0x1_phys_0", 100), ("pid_12_luid_0x0_0x2_phys_0", 50), ("pid_7_luid_0x0_0x1_phys_0", 300), ("total", 9)])
            .Should().Equal((7, 300L), (12, 150L));
    }
}
