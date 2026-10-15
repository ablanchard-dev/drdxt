using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Un PC de bureau sans jeu gagne aussi en latence et en fluidité : une machine de bureau
/// synthétique (i5 à carte intégrée, une barrette de 8 Go, écran 60 Hz, aucun jeu) reçoit ses constats (mémoire saturée
/// avec ses gestes, simple canal, programmes au démarrage) et ses leviers (accélération du pointeur, Mode Jeu, Game DVR),
/// et aucun levier de carte graphique dédiée. Le plan d'alimentation reste fermé : il ne s'ouvre que sur une mesure en
/// jeu qui montre le processeur comme goulot (décision mesurée du 10/09/2026 : Hautes performances coûte du courant au
/// repos et le parcage est identique entre les deux plans) ; sans mesure, il se dit non lu plutôt que de promettre.
/// </summary>
public sealed class BureauSansJeuTests
{
    private const string Nom = "bureau-sans-jeu-i5-8go";

    private static IReadOnlyList<ITweak> VraisLeviers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All().ToList();
    }

    [Fact]
    public void La_memoire_saturee_est_dite_avec_ses_gestes_et_le_simple_canal_aussi()
    {
        var m = MachinesEnregistreesTests.Charger(Nom);

        m.Inventaire.Gpus.Should().OnlyContain(g => g.Vendor == "Intel", "carte intégrée seulement");
        m.Inventaire.Memory.Should().ContainSingle("une barrette : simple canal");
        var memoire = m.Sante.Should().ContainSingle(s => s.Domaine == MemoireSaturee.Domaine && s.Nom == MemoireSaturee.Nom).Subject;
        memoire.Statut.Should().Be(RepairStatus.Suspicious);
        memoire.Action.Should().Be(RepairAction.Recommandation);
        // Le constat enregistré est celui que le code produit pour ces chiffres : la fixture ne raconte pas sa propre histoire.
        memoire.Should().Be(MemoireSaturee.Juger(new MemoireLue(8UL << 30, 450UL << 20, 11UL << 30, 12UL << 30)));
        m.Sante.Should().Contain(s => s.Nom == "programmes au démarrage" && s.Action == RepairAction.Recommandation);
    }

    [Fact]
    public void Les_leviers_du_bureau_s_ouvrent_et_aucun_levier_de_carte_dediee()
    {
        var m = MachinesEnregistreesTests.Charger(Nom);
        var ouverts = VraisLeviers().Where(t => t.AppliesTo(m.Inventaire, m.Reglages)).Select(t => t.Id).ToList();

        ouverts.Should().Contain("latency.mouse_acceleration_off", "la souris redevient proportionnelle au geste");
        ouverts.Should().NotContain("fixit.power_plan", "sans mesure en jeu, aucune preuve que Hautes performances rende quelque chose");
        VraisLeviers().Single(t => t.Id == "fixit.power_plan").EtatNonLu(m.Reglages).Should().BeTrue("il se dit non lu, pas « déjà bon »");
        ouverts.Should().NotContain(["gpu.power_limit", "latency.prefer_max_perf", "latency.vrr_vsync_cap", "latency.render_queue_cap", "fixit.x3d_ccd"],
            "rien de ce qui suppose une carte dédiée, un écran VRR ou un jeu");
    }
}
