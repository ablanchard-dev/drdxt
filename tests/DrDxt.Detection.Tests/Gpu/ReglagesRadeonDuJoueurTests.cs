using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Le pendant AMD des options NVIDIA du joueur : Boost, Image Sharpening, RSR et AFMF, lus et nommés, jamais posés.
public sealed class ReglagesRadeonDuJoueurTests
{
    private static readonly GpuInfo Radeon = new("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.", 16L << 30, "32.0", TriState.On);
    private static readonly GpuInfo GeForce = new("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "581.57", TriState.On);
    private static IDisposable Fr() => Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));

    [Fact]
    public void Sur_une_Radeon_les_reglages_lus_se_nomment_sans_etre_poses()
    {
        using var _ = Fr();
        var lus = new AdlxReglagesLus(BoostActif: false, ImageSharpeningActif: true, RsrActif: false, AfmfActif: true);

        var f = ReglagesRadeonDuJoueur.Juger([Radeon], lus).Single();

        f.Domaine.Should().Be("GPU DRIVER");
        f.Nom.Should().Be(ReglagesRadeonDuJoueur.Nom);
        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().NotBe(RepairAction.ReparationAutomatique);
        f.ValeurCourante.Should().Be(Docteur.T("RAD.Valeur", Docteur.T("RAD.Eteint"), Docteur.T("RAD.Actif"), Docteur.T("RAD.Eteint"), Docteur.T("RAD.Actif")));
        f.Explication.Should().Contain("HYPR-RX");
    }

    [Fact]
    public void Un_reglage_non_lu_se_dit_non_lu_jamais_eteint()
    {
        using var _ = Fr();
        var f = ReglagesRadeonDuJoueur.Juger([Radeon], new AdlxReglagesLus(ImageSharpeningActif: true)).Single();
        f.ValeurCourante.Should().Be(Docteur.T("RAD.Valeur", Docteur.T("RAD.NonLu"), Docteur.T("RAD.Actif"), Docteur.T("RAD.NonLu"), Docteur.T("RAD.NonLu")));
    }

    [Fact]
    public void Rien_de_lu_se_dit_non_lu_avec_sa_raison()
    {
        var f = ReglagesRadeonDuJoueur.Juger([Radeon], AdlxReglagesLus.RienLu("amdadlx64.dll absent")).Single();
        f.Statut.Should().Be(RepairStatus.Unknown);
        f.Explication.Should().Contain("amdadlx64.dll absent");
    }

    [Fact]
    public void Sans_Radeon_dediee_rien_n_est_dit()
    {
        ReglagesRadeonDuJoueur.Juger([GeForce], new AdlxReglagesLus(BoostActif: true)).Should().BeEmpty();
        ReglagesRadeonDuJoueur.Juger([], new AdlxReglagesLus(BoostActif: true)).Should().BeEmpty();
    }

    [Fact]
    public void Le_nom_du_constat_se_traduit()
        => RepairLibelles.Nom(ReglagesRadeonDuJoueur.Nom).Should().NotBeNullOrEmpty();
}
