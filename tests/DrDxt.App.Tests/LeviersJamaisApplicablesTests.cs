using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// V3, autocontrôle du 08/10/2026 : le crible d'<c>AucunLevierMortTests</c> lit le texte et ne voit qu'un « => false » écrit tel
/// quel. Ici, chaque levier du VRAI catalogue est interrogé : <c>AppliesTo</c> sur chaque machine enregistrée (réglages lus,
/// puis tous les tri-états à « Off »), puis sur des scénarios nommés bâtis sur la machine de référence. Un levier qui ne
/// s'ouvre nulle part n'a sa place qu'avec sa raison écrite ici.
/// Limite (revue du 08/10/2026, REV5) : les états sont fabriqués (tri-états de premier niveau à « Off ») ; un levier dont
/// le VRAI lecteur ne rend jamais l'état qui l'ouvre reste vert ici. Ce qui le couvre : les machines enregistrées et le
/// rejeu des vrais scans, pas cette garde.
/// </summary>
public sealed class LeviersJamaisApplicablesTests
{
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["latency.reflex"] = "défait seulement l'écriture d'une ancienne version (voir AucunLevierMortTests)",
        ["gpu.fan_curve"] = "jamais proposé par l'autopilote ; le ventilateur s'écrit en mode Expert (VentilateurExpert)",
        ["latency.render_queue_cap"] = "s'ouvre sur la file lue au profil du jeu en cours (pilote NVIDIA ou ADLX) : RenderQueueCapTweakTests",
        ["latency.vrr_vsync_cap"] = "s'ouvre sur un écran VRR lu par le pilote : VrrVsyncCapConfigTweakTests, VrrVsyncCapAmdTests",
        ["fixit.heures_actives"] = "s'ouvre sur des heures actives « manuelles » lues et des parties datées dans le fuseau local : HeuresActivesTweakTests",
    };

    private const string Equilibre = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static readonly Dictionary<string, Func<HardwareInventory, SettingsState, (HardwareInventory, SettingsState)>> Scenarios = new()
    {
        ["fixit.ecran_frequence"] = (hw, s) => (hw, s with { DisplayRefresh = new RefreshHeadroom(TriState.On, 60, 144) }),
        ["fixit.power_plan"] = (hw, s) => (hw, s with { PowerPlanGuid = Equilibre, GoulotMesure = BottleneckState.CpuBoundStrong }),
        ["fixit.mode_boost"] = (hw, s) => (hw, s with { PowerPlanGuid = Equilibre, ModeDeBoost = new ModeDeBoostLu(Guid.Parse(Equilibre), 0, 2) }),
        ["latency.mouse_acceleration_off"] = (hw, s) => (hw, s with { AccelerationSouris = TriState.On }),
        ["fixit.x3d_ccd"] = (hw, s) => (hw with { Cpu = new CpuInfo("AMD Ryzen 9 7950X3D", "AuthenticAMD", 16, 32, true, 2) },
            s with { ParkedCoreIndexes = [] }),
    };

    private static IReadOnlyList<ITweak> VraisLeviers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All().ToList();
    }

    // Tous les réglages tri-états à « Off » : la machine qui a besoin de tout.
    private static SettingsState ToutAOff(SettingsState s)
    {
        var copie = (SettingsState)typeof(SettingsState).GetMethod("<Clone>$")!.Invoke(s, null)!;
        foreach (var p in typeof(SettingsState).GetProperties().Where(p => p.PropertyType == typeof(TriState) && p.CanWrite))
            p.SetValue(copie, TriState.Off);
        return copie;
    }

    private static bool SApplique(ITweak t, HardwareInventory hw, SettingsState s)
    {
        try { return t.AppliesTo(hw, s); }
        catch (Exception) { return false; }
    }

    private static List<string> JamaisApplicables(IEnumerable<ITweak> leviers)
    {
        var machines = MachinesEnregistreesTests.Noms().Cast<object[]>().Select(n => MachinesEnregistreesTests.Charger((string)n[0])).ToList();
        var reference = MachinesEnregistreesTests.Charger("reference-i7-14700f-rtx4060-2026-10-06");
        return leviers.Where(t =>
                !machines.Any(m => SApplique(t, m.Inventaire, m.Reglages) || SApplique(t, m.Inventaire, ToutAOff(m.Reglages)))
                && !(Scenarios.TryGetValue(t.Id, out var sc) && sc(reference.Inventaire, reference.Reglages) is var (hw, st)
                     && SApplique(t, hw, st)))
            .Select(t => t.Id).ToList();
    }

    [Fact]
    public void Aucun_levier_du_vrai_catalogue_ne_reste_ferme_sans_raison()
        => JamaisApplicables(VraisLeviers()).Except(Exceptions.Keys).Should().BeEmpty(
            "un levier qui ne s'ouvre ni sur une machine enregistrée ni sur son scénario est mort, ou listé avec sa raison");

    [Fact]
    public void Chaque_exception_est_encore_fermee()
    {
        var fermes = JamaisApplicables(VraisLeviers());
        fermes.Should().NotBeEmpty("témoin : le crible interroge bien les leviers");
        Exceptions.Keys.Except(fermes).Should().BeEmpty("une exception qui s'ouvre se retire de la liste");
    }

    [Fact]
    public void Un_levier_qui_ne_s_applique_jamais_est_vu()
    {
        var mort = Substitute.For<ITweak>();
        mort.Id.Returns("test.mort");
        mort.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(false);
        JamaisApplicables(VraisLeviers().Append(mort)).Should().Contain("test.mort");
    }
}
