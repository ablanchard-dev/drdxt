using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// A5 (07/10/2026) : une seule lecture de la température de carte pour NVIDIA, AMD et Intel, par la bibliothèque du
// pilote ; rien n'est lu sur la vraie machine ici (chaque lecteur est une doublure).
public sealed class TemperatureCarteTests
{
    private static readonly TemperatureCarte.Lecteur Absent = () => null;
    private static TemperatureCarte.Lecteur Rend(double? c, string? raison = null) => () => (c, raison);
    private static GpuInfo G(string modele, long vram = 8L << 30) => new(modele, "", vram, "", TriState.Unknown);

    [Fact]
    public void Chaque_marque_rend_sa_temperature_avec_sa_source()
    {
        TemperatureCarte.Choisir(Rend(61), Absent, Absent).Should().Be(new TemperatureCarteLue(61, "NVML", null));
        TemperatureCarte.Choisir(Absent, Rend(70.4), Absent).Should().Be(new TemperatureCarteLue(70.4, "ADLX", null));
        TemperatureCarte.Choisir(Absent, Absent, Rend(55)).Should().Be(new TemperatureCarteLue(55, "IGCL", null));
    }

    // Le pilote NVIDIA est là mais muet : la puce Intel d'à côté ne parle pas à sa place sous le nom de « la carte ».
    [Fact]
    public void Une_bibliotheque_presente_et_muette_ne_laisse_pas_parler_la_suivante()
    {
        var lu = TemperatureCarte.Choisir(Rend(null, "NVML n'a pas rendu de température"), Absent, Rend(48));

        lu.Celsius.Should().BeNull();
        lu.Source.Should().Be("NVML");
        lu.Raison.Should().Be("NVML n'a pas rendu de température");
    }

    // Revue F-37 : une Radeon muette à côté d'une puce Intel : ADLX répond, IGCL ne parle pas à sa place.
    [Fact]
    public void Une_carte_amd_muette_ne_laisse_pas_parler_la_puce_intel()
    {
        var lu = TemperatureCarte.Choisir(Absent, Rend(null, "muette"), Rend(48));

        lu.Source.Should().Be("ADLX");
        lu.Celsius.Should().BeNull();
    }

    // Revue F-37 : au-delà de 150 °C, ce n'est pas une température (valeur hors unité, champ mal lu).
    [Fact]
    public void Une_valeur_de_150_ou_plus_n_est_pas_une_temperature()
    {
        TemperatureCarte.Choisir(Rend(149), Absent, Absent).Celsius.Should().Be(149);
        TemperatureCarte.Choisir(Rend(150), Absent, Absent).Celsius.Should().BeNull();
        TemperatureCarte.Choisir(Rend(1499), Absent, Absent).Celsius.Should().BeNull();
    }

    // Non lu n'est jamais froid : zéro ou une exception restent « non lu », avec la raison.
    [Fact]
    public void Zero_ou_une_exception_restent_non_lus()
    {
        TemperatureCarte.Choisir(Absent, Rend(0), Absent).Celsius.Should().BeNull();
        var plante = TemperatureCarte.Choisir(Absent, Absent, () => throw new DllNotFoundException());
        plante.Celsius.Should().BeNull();
        plante.Raison.Should().Contain("DllNotFoundException");
        TemperatureCarte.Choisir(Absent, Absent, Absent).Should().Be(new TemperatureCarteLue(null, null,
            "aucune bibliothèque de pilote graphique (NVML, ADLX, IGCL) sur ce PC"));
    }

    // ── le docteur ──

    [Fact]
    public void Une_temperature_Intel_lue_par_IGCL_devient_un_constat_observe()
    {
        var f = RepairAuditService.TemperatureIntel([G("Intel(R) UHD Graphics 770", 128L << 20), G("Intel(R) Arc(TM) B580 Graphics")],
            (66.6, null))!;
        f.Statut.Should().Be(RepairStatus.Observed);
        f.ValeurCourante.Should().Be("67 °C");
        f.Explication.Should().Contain("IGCL");
        RepairAuditService.TemperatureIntel([G("Intel(R) Arc(TM) B580 Graphics")], (null, "ctlInit a échoué")).Should().BeNull();
        RepairAuditService.TemperatureIntel([G("Intel(R) Arc(TM) B580 Graphics")], (0, null)).Should().BeNull();
        // Une carte NVIDIA ou AMD a sa propre bibliothèque : la puce Intel n'est pas « la carte ».
        RepairAuditService.TemperatureIntel([G("Intel(R) UHD Graphics 770"), G("NVIDIA GeForce RTX 4060")], (50, null)).Should().BeNull();
        RepairAuditService.TemperatureIntel([G("Intel(R) UHD Graphics 770"), G("AMD Radeon RX 7800 XT")], (50, null)).Should().BeNull();
    }

    [Fact]
    public void Une_carte_Intel_non_lue_est_nommee_avec_la_raison_d_IGCL()
        => RepairAuditService.TemperatureCarteNonLue([G("Intel(R) Arc(TM) B580 Graphics")], new(), "ControlLib.dll absente de System32.")
            .Should().Be("DrDXT n'a pas pu lire la température de ta carte (Intel(R) Arc(TM) B580 Graphics) par le pilote Intel "
                + "(ControlLib.dll absente de System32). Le logiciel Intel Graphics ou HWiNFO la montre.");

    // La machine enregistrée `intel-arc-synthetique` porte cette phrase : elle doit rester celle que le code écrit.
    [Fact]
    public void La_phrase_de_la_machine_Intel_enregistree_est_celle_du_code()
        => RepairAuditService.TemperatureCarteNonLue([G("Intel(R) Arc(TM) B580 Graphics")], new()).Should().Be(
            "DrDXT n'a pas pu lire la température de ta carte (Intel(R) Arc(TM) B580 Graphics) par le pilote Intel. "
            + "Le logiciel Intel Graphics ou HWiNFO la montre.");
    // 07/10/2026 : IGCL n'a jamais tourné sur une vraie carte Intel ; toute puce Intel installe ControlLib.dll. Rouvrir ce
    // lecteur est une décision, après un essai sur du vrai matériel : ce test doit changer avec elle.
    [Fact]
    public void Le_lecteur_Intel_reste_ferme_tant_qu_il_n_a_pas_ete_vu_sur_une_vraie_carte()
    {
        var ouvert = TemperatureCarte.IgclOuvert;
        ouvert.Should().BeFalse();
        TemperatureCarte.IgclFerme.Should().Contain("non lu");
    }
}
