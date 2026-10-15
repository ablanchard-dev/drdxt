using DrDxt.App.Profiles;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le vrai levier des heures d'activité passe par le vrai profil générique et le vrai consommateur du
/// scan. « Optimiser » le propose seulement quand des parties relevées sortent des heures lues, sur toutes les machines (le
/// réglage est le même chez NVIDIA, AMD, Intel, portable) ; sur les cinq machines enregistrées, qui n'ont ni heures ni
/// parties lues, il n'est jamais proposé. Source : Microsoft Learn, « Manage device restarts after updates ».
/// </summary>
public sealed class HeuresActivesOptimiserTests
{
    private const string Id = "fixit.heures_actives";
    private static readonly TimeSpan Ete = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("paris-ete", Ete, "paris-ete", "paris-ete");
    private static readonly PlageDeJeu Du0410 = new(new DateTimeOffset(2026, 10, 4, 8, 23, 48, Ete), new DateTimeOffset(2026, 10, 4, 8, 44, 3, Ete));

    private static IReadOnlyList<string> Plan(HardwareInventory hw, SettingsState st)
    {
        var levier = new HeuresActivesTweak(Substitute.For<IRegistryReader>(), Substitute.For<IElevatedClient>(), () => [Du0410], Paris);
        var c = Substitute.For<ITweakCatalog>();
        c.Get(Arg.Any<string>()).Returns(_ => Result<ITweak>.Fail("TWEAK.NOT_FOUND", "absent du test"));
        c.Get(Id).Returns(Result<ITweak>.Ok(levier));
        c.All().Returns([levier]);
        return new ScanProfileConsumer(new ChipProfileMatcher(new ChipProfileCatalog()), c,
                new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90)))
            .Consume(hw, st).Plan.TweakIds;
    }

    private static MachinesEnregistreesTests.Machine Reference() => MachinesEnregistreesTests.Charger("reference-i7-14700f-rtx4060-2026-10-06");

    [Fact]
    public void Sur_la_machine_de_reference_avec_ses_heures_et_sa_partie_reelles_Optimiser_le_propose()
    {
        var m = Reference();
        Plan(m.Inventaire, m.Reglages with { HeuresActives = new(15, 8, false), HeuresDeJeu = [Du0410] }).Should().Contain(Id);
    }

    [Fact]
    public void Parties_deja_couvertes_ou_mode_automatique_rien()
    {
        var m = Reference();
        Plan(m.Inventaire, m.Reglages with { HeuresActives = new(6, 15, false), HeuresDeJeu = [Du0410] }).Should().NotContain(Id);
        Plan(m.Inventaire, m.Reglages with { HeuresActives = new(15, 8, true), HeuresDeJeu = [Du0410] }).Should().NotContain(Id);
        Plan(m.Inventaire, m.Reglages with { HeuresActives = new(15, 8, false), HeuresDeJeu = [] }).Should().NotContain(Id);
    }

    [Theory]
    [MemberData(nameof(MachinesEnregistreesTests.Noms), MemberType = typeof(MachinesEnregistreesTests))]
    public void Les_machines_enregistrees_ne_recoivent_aucune_proposition_fausse(string nom)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        Plan(m.Inventaire, m.Reglages).Should().NotContain(Id, "ni heures ni parties lues : non lu n'est pas un besoin");
        // Les mêmes, avec les heures et la partie de la référence : proposé partout, quelle que soit la carte ou le châssis.
        Plan(m.Inventaire, m.Reglages with { HeuresActives = new(15, 8, false), HeuresDeJeu = [Du0410] }).Should().Contain(Id);
    }
}
