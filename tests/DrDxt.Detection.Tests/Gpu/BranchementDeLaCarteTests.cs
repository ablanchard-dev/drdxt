using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// La carte graphique est-elle branchée comme elle le peut ? (a) le lien PCIe, lu par
// Windows sans pilote (DEVPKEY_PciDevice_*, relevé : RTX 4060 en Gen4 x8, son maximum) ; (b) l'écran branché sur la sortie
// de la carte mère alors qu'une carte dédiée est là.
public sealed class BranchementDeLaCarteTests
{
    private static BranchementDeLaCarte.Lien L(int largeurMax, int largeur, int genMax = 4, int gen = 4)
        => new("NVIDIA GeForce RTX 4070", largeurMax, largeur, genMax, gen);

    [Fact]
    public void Un_lien_a_son_maximum_se_dit_sans_geste()
    {
        var c = BranchementDeLaCarte.JugerLien(L(8, 8)).Single();

        c.Nom.Should().Be("lien PCIe de NVIDIA GeForce RTX 4070");
        c.ValeurCourante.Should().Be("x8, PCIe 4.0");
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Une_carte_x16_qui_tourne_en_x4_est_signalee_avec_les_causes_possibles()
    {
        var c = BranchementDeLaCarte.JugerLien(L(16, 4)).Single();

        c.ValeurCourante.Should().Be("x4, PCIe 4.0");
        c.ValeurAttendue.Should().Be("x16");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("emplacement").And.Contain("rallonge").And.Contain("manuel");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    // Sur un portable, la puce est soudée et câblée par le constructeur (RTX 4080 Laptop en x8) :
    // « mal enfoncée, rallonge » serait faux. La largeur est dite, pas jugée ; batterie non lue ⇒ rien.
    [Fact]
    public void Sur_un_portable_une_largeur_plus_basse_est_dite_sans_accuser_l_emplacement()
    {
        var c = BranchementDeLaCarte.JugerLien(L(16, 8), batterie: true).Single();

        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().NotContain("enfoncée").And.NotContain("rallonge");
        // Batterie non lue ⇒ le lien est dit, non jugé ; à son maximum, la phrase habituelle.
        var nonLu = BranchementDeLaCarte.JugerLien(L(16, 8), batterie: null).Single();
        nonLu.Statut.Should().Be(RepairStatus.Unknown);
        nonLu.ValeurCourante.Should().Be("x8, PCIe 4.0");
        BranchementDeLaCarte.JugerLien(L(8, 8), batterie: null).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    // Une carte dédiée de 1 Go (GT 710, Quadro) à côté d'une RTX n'est pas la carte mère.
    [Fact]
    public void Une_petite_carte_dediee_n_est_pas_prise_pour_la_carte_mere()
        => BranchementDeLaCarte.JugerEcrans(["NVIDIA GeForce GT 710"],
                [new GpuInfo("NVIDIA GeForce RTX 4070", "", 12L << 30, "", TriState.Unknown),
                 new GpuInfo("NVIDIA GeForce GT 710", "", 1L << 30, "", TriState.Unknown)], batterie: false)
            .Should().BeEmpty();

    // Un 2e écran sur la carte mère ne dit rien du jeu sur l'écran principal : la phrase parle de « cet écran ».
    [Fact]
    public void L_ecran_sur_la_carte_mere_est_nomme_sans_dire_que_le_jeu_y_passe()
        => BranchementDeLaCarte.JugerEcrans(["Intel(R) UHD Graphics 770"],
                [new GpuInfo("NVIDIA GeForce RTX 4070", "", 12L << 30, "", TriState.Unknown),
                 new GpuInfo("Intel(R) UHD Graphics 770", "", 128L << 20, "", TriState.Unknown)], batterie: false)
            .Single().Explication.Should().Contain("sur cet écran").And.NotContain("le jeu passe par");

    // La génération baisse au repos pour économiser : elle n'est pas jugée, seulement dite.
    [Fact]
    public void Une_generation_plus_basse_n_est_pas_un_defaut()
        => BranchementDeLaCarte.JugerLien(L(8, 8, genMax: 4, gen: 1)).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Un_lien_non_lu_ne_dit_rien()
        => BranchementDeLaCarte.JugerLien(L(0, 0)).Should().BeEmpty();

    private static readonly GpuInfo Rtx = new("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "617.14", TriState.On);
    private static readonly GpuInfo Uhd = new("Intel(R) UHD Graphics 770", "Intel", 128L << 20, "31.0", TriState.Unknown);

    [Fact]
    public void Un_ecran_sur_la_sortie_de_la_carte_mere_avec_une_carte_dediee_est_signale()
    {
        var c = BranchementDeLaCarte.JugerEcrans(["Intel(R) UHD Graphics 770"], [Rtx, Uhd], batterie: false).Single();

        c.Nom.Should().Be("écran branché sur la carte mère");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("NVIDIA GeForce RTX 4070");
    }

    // L'écran principal sur la carte dédiée, un second sur la carte mère : un montage voulu (stream, discussion), à regarder.
    // L'écran principal sur la carte mère, même avec un second sur la carte dédiée : le jeu s'y ouvre, à corriger. Principal
    // non lu : un écran sur la carte dédiée ne démontre rien de nuisible, à regarder.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", RepairStatus.NonDefault)]
    [InlineData("Intel(R) UHD Graphics 770", RepairStatus.Suspicious)]
    [InlineData(null, RepairStatus.NonDefault)]
    public void Un_second_ecran_sur_la_carte_mere_se_juge_sur_l_ecran_principal(string? principal, RepairStatus attendu)
        => BranchementDeLaCarte.JugerEcrans([Rtx.Model, "Intel(R) UHD Graphics 770"], [Rtx, Uhd], batterie: false, principal).Single()
            .Statut.Should().Be(attendu);

    [Fact]
    public void Sur_la_carte_dediee_ou_sur_un_portable_rien_n_est_dit()
    {
        BranchementDeLaCarte.JugerEcrans(["NVIDIA GeForce RTX 4070"], [Rtx, Uhd], batterie: false).Should().BeEmpty();
        // Un portable affiche par la puce intégrée et calcule sur la carte dédiée : c'est normal.
        BranchementDeLaCarte.JugerEcrans(["Intel(R) UHD Graphics 770"], [Rtx, Uhd], batterie: true).Should().BeEmpty();
        BranchementDeLaCarte.JugerEcrans(["Intel(R) UHD Graphics 770"], [Rtx, Uhd], batterie: null).Should().BeEmpty();
    }

    [Fact]
    public void Sans_carte_dediee_la_sortie_de_la_carte_mere_est_la_seule()
        => BranchementDeLaCarte.JugerEcrans(["Intel(R) UHD Graphics 770"], [Uhd], batterie: false).Should().BeEmpty();
}
