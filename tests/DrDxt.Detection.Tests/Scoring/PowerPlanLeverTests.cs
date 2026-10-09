using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Lot 1 « preuve du besoin » — le plan d'alimentation n'est plus un levier du registre.
///
/// Le rapport genere sur la machine de reference le 10/09/2026 annoncait « 1 probleme prouve :
/// power-plan » sur la foi de coeurs parques — alors que le parking est identique entre Equilibre et
/// Haute performance. Le scanner prouvait au client un besoin que notre propre mesure disculpe.
///
/// Un plan Equilibre n'est pas un defaut de CONFIGURATION ; il ne devient une piste que si la mesure
/// dit que le processeur limite le jeu — et ce jugement appartient au candidat `fixit.power_plan`,
/// pas au registre (la mesure ne bouge jamais le registre : `MeasurementDoesNotMoveTheScoreTests`).
/// </summary>
public class PowerPlanLeverTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static HardwareInventory Hw() => new(
        new CpuInfo("Intel Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("RTX 4060", "NVIDIA", 8L << 30, "616.56", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 3800, 4000) },
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState Equilibre() => new(
        TriState.On, TriState.On, Balanced, TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: new[] { 16, 17, 18 }, Vrr: TriState.Unknown);

    [Theory]
    [InlineData(null)]
    [InlineData(BottleneckState.CpuBoundStrong)]
    [InlineData(BottleneckState.GpuBound)]
    public void Equilibre_avec_coeurs_parques_n_est_JAMAIS_un_probleme_de_configuration(BottleneckState? goulot)
    {
        var mesure = goulot is { } g ? new BottleneckVerdict(g, BottleneckConfidence.High, "motif", false) : (BottleneckVerdict?)null;

        LeverCatalog.Evaluate(Hw(), Equilibre(), mesure)
            .Should().NotContain(l => l.LeverId == "power-plan",
                "le parking est identique entre les plans, et la mesure ne fabrique pas un defaut de configuration");
    }
}
