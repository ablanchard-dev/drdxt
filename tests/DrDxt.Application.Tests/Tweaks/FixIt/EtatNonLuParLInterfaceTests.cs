using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

/// <summary>
/// Revue n° 14 (08/10/2026) : la liste « ce que DrDXT fait ici » disait « déjà bon » pour un réglage que le scan n'avait
/// pas pu lire. L'état non lu est demandé par l'interface (<see cref="ITweak"/>), comme le fait la liste : une méthode
/// d'un dérivé que l'interface n'atteint pas ne compte pas.
/// </summary>
public sealed class EtatNonLuParLInterfaceTests
{
    private static readonly IElevatedClient Eleve = Substitute.For<IElevatedClient>();

    private static SettingsState Lu() => new(
        TriState.On, TriState.On, KnownPowerPlans.HighPerformance.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On, PagefileConfigured: TriState.On);

    public static TheoryData<string> Leviers => new() { "boost", "x3d", "plan", "pagefile" };

    private static (ITweak Levier, SettingsState NonLu) Cas(string nom) => nom switch
    {
        "boost" => (new ModeBoostTweak(Substitute.For<IPowerPlanReader>(), Eleve), Lu() with { ModeDeBoost = null }),
        "x3d" => (new X3dCcdParkingFixTweak(Substitute.For<IServiceStateReader>(), Substitute.For<IRegistryReader>(), Eleve),
            Lu() with { ParkedCoreIndexes = null }),
        "plan" => (new SanePowerPlanTweak(Substitute.For<IPowerPlanReader>(), Eleve), Lu() with { PowerPlanGuid = "" }),
        _ => (new SanePagefileTweak(Substitute.For<IRegistryReader>(), Eleve), Lu() with { PagefileConfigured = TriState.Unknown }),
    };

    [Theory]
    [MemberData(nameof(Leviers))]
    public void Un_etat_non_lu_est_vu_par_l_interface(string nom)
    {
        var (levier, nonLu) = Cas(nom);
        levier.EtatNonLu(nonLu).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Leviers))]
    public void Un_etat_lu_n_est_pas_non_lu(string nom)
    {
        var (levier, _) = Cas(nom);
        var lu = nom == "boost" ? Lu() with { ModeDeBoost = new ModeDeBoostLu(KnownPowerPlans.HighPerformance, 2, 2) } : Lu();
        levier.EtatNonLu(lu).Should().BeFalse();
    }

    [Fact]
    public void Equilibre_sans_mesure_en_jeu_n_est_pas_deja_bon()
    {
        ITweak plan = new SanePowerPlanTweak(Substitute.For<IPowerPlanReader>(), Eleve);
        var equilibre = Lu() with { PowerPlanGuid = KnownPowerPlans.Balanced.ToString() };

        plan.EtatNonLu(equilibre).Should().BeTrue("le goulot mesuré en jeu décide");
        plan.EtatNonLu(equilibre with { GoulotMesure = BottleneckState.GpuBound }).Should().BeFalse();
    }
}
