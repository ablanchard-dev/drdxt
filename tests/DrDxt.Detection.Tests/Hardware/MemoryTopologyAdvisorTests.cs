using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Les chaines d'emplacement de ces tests ne sont pas inventees : `Controller0-DIMMA2` et
/// `Controller1-DIMMB2` sont ce que `Win32_PhysicalMemory` a rendu sur la machine de reference,
/// avec `Attributes=2` (dual rank) et 3800 MT/s a 1350 mV. Un test de topologie ecrit
/// sur des chaines imaginaires prouve la lecture d'une invention.
/// </summary>
public sealed class MemoryTopologyAdvisorTests
{
    private static MemoryModule Module(string? locator, string part = "F4-4000C18-32GTES")
        => new(part, "G.Skill", ConfiguredMhz: 3800, RatedMhz: 4000,
               SmbiosMemoryType: 26, Ranks: 2, ConfiguredVoltageMv: 1350, DeviceLocator: locator);

    [Fact]
    public void OneStickIsSingleChannelAndNeedsNoStringParsingToProveIt()
    {
        var advice = MemoryTopologyAdvisor.Advise(new[] { Module("Controller0-DIMMA2") }, ChassisType.Desktop);

        advice.Should().ContainSingle().Which.MessageKey.Should().Be("Finding.Memory.SingleChannelOneModule");
    }

    [Fact]
    public void TheAdviceCarriesTheSourceThatQuantifiesTheCost()
    {
        var advice = MemoryTopologyAdvisor.Advise(new[] { Module("Controller0-DIMMA2") }, ChassisType.Desktop);

        advice.Single().SourceUrl.Should().Be(MemoryTopologyAdvisor.ChannelSourceUrl);
    }

    [Fact]
    public void TheReferenceMachineIsCorrectlyPopulatedAndGetsNoAdvice()
    {
        // Deux controleurs distincts, deux lettres de canal distinctes : rien a dire.
        var advice = MemoryTopologyAdvisor.Advise(new[]
        {
            Module("Controller0-DIMMA2"),
            Module("Controller1-DIMMB2"),
        }, ChassisType.Desktop);

        advice.Should().BeEmpty();
    }

    [Fact]
    public void TwoSticksOnTheSameControllerAreSingleChannelPopulation()
    {
        var advice = MemoryTopologyAdvisor.Advise(new[]
        {
            Module("Controller0-DIMMA1"),
            Module("Controller0-DIMMA2"),
        }, ChassisType.Desktop);

        advice.Should().ContainSingle().Which.MessageKey.Should().Be("Finding.Memory.SingleChannelPopulation");
    }

    [Fact]
    public void TheChannelLetterAloneIsEnoughWhenTheBoardDoesNotNameItsControllers()
    {
        var advice = MemoryTopologyAdvisor.Advise(new[] { Module("DIMMA1"), Module("DIMMA2") }, ChassisType.Desktop);

        advice.Should().ContainSingle().Which.MessageKey.Should().Be("Finding.Memory.SingleChannelPopulation");
        MemoryTopologyAdvisor.Advise(new[] { Module("DIMMA1"), Module("DIMMB1") }, ChassisType.Desktop).Should().BeEmpty();
    }

    [Fact]
    public void AnUnreadableLocatorSaysUnknownAndNeverAccusesTheClientOfSingleChannel()
    {
        // Le faux positif est la faute chere ici : il envoie demonter une machine bien montee.
        foreach (var locator in new string?[] { null, "", "SODIMM", "Emplacement 3" })
        {
            var advice = MemoryTopologyAdvisor.Advise(new[] { Module(locator), Module(locator) }, ChassisType.Desktop);

            advice.Should().ContainSingle("un emplacement illisible ({0}) doit se DIRE, pas se taire", locator)
                .Which.MessageKey.Should().Be("Finding.Memory.ChannelTopologyUnknown");
        }
    }

    [Fact]
    public void OneUnreadableLocatorAmongReadableOnesStillYieldsUnknown()
    {
        // Conclure sur l'echantillon lisible serait exactement le defaut que cette boucle
        // a trouve cinq fois : une regle appliquee a une partie de ce qu'elle couvre.
        var advice = MemoryTopologyAdvisor.Advise(new[] { Module("Controller0-DIMMA2"), Module("SODIMM") }, ChassisType.Desktop);

        advice.Should().ContainSingle().Which.MessageKey.Should().Be("Finding.Memory.ChannelTopologyUnknown");
    }

    [Fact]
    public void OneControllerNameForEverySlotIsNotSingleChannelWhenTheLettersDiffer()
    {
        // Le faux positif le plus cher du conseiller : certaines cartes ecrivent `Controller0`
        // partout et ne distinguent les canaux que par la lettre. Trancher sur le seul controleur
        // accuserait de simple canal une machine parfaitement montee.
        MemoryTopologyAdvisor.Advise(
            new[] { Module("Controller0-DIMMA2"), Module("Controller0-DIMMB2") }, ChassisType.Desktop)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    [InlineData(ChassisType.Unknown)]
    public void OutsideADesktopTheProductSaysNothingAboutTopology(ChassisType chassis)
    {
        // Une seule entree sur un portable est souvent de la memoire soudee multi-canal : le
        // simple canal y serait un mensonge, et une barrette a ajouter une impossibilite.
        MemoryTopologyAdvisor.Advise(new[] { Module("Controller0-DIMMA2") }, chassis)
            .Should().BeEmpty();
    }

    [Fact]
    public void NoModulesReadMeansSilence()
    {
        MemoryTopologyAdvisor.Advise(Array.Empty<MemoryModule>(), ChassisType.Desktop).Should().BeEmpty();
    }
}

/// <summary>
/// Le conseil que personne ne lit n'existe pas.
///
/// C'est exactement le defaut que ce lot a trouve : `DeviceLocator` etait lu, porte par le
/// contrat, documente comme decisif, et consomme par personne. Un conseiller de plus dans le
/// meme etat serait la meme dette avec un nom plus recent. Ce test relie le conseiller au seul
/// endroit d'ou un constat atteint le client.
/// </summary>
public sealed class MemoryTopologyReachesTheScanTests
{
    private static HardwareInventory Inventory(params string?[] locators) => new(
        new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        Array.Empty<GpuInfo>(),
        locators.Select(l => new MemoryModule("F4-4000C18-32GTES", "G.Skill", 3800, 4000,
            SmbiosMemoryType: 26, Ranks: 2, ConfiguredVoltageMv: 1350, DeviceLocator: l)).ToList(),
        new MotherboardInfo("ASUS", "TUF GAMING B760"),
        new BiosInfo("1.2.3", DateTimeOffset.UnixEpoch, true),
        ChassisType.Desktop);

    private static SettingsState AnyState() => new(
        TriState.On, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    [Fact]
    public void ASingleModuleProducesAFindingTheClientCanRead()
    {
        var findings = ScoringRules.Evaluate(Inventory("Controller0-DIMMA2"), AnyState());

        findings.Should().Contain(f => f.TitleKey == "Finding.Memory.SingleChannelOneModule");
    }

    [Fact]
    public void TheReferenceMachineProducesNoMemoryTopologyFinding()
    {
        var findings = ScoringRules.Evaluate(Inventory("Controller0-DIMMA2", "Controller1-DIMMB2"), AnyState());

        findings.Should().NotContain(f => f.TitleKey.StartsWith("Finding.Memory.SingleChannel"));
    }
}
