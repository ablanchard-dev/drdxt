using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Scoring;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.EdgeCases;

/// <summary>
/// Matériel ancien, non-standard ou données WMI dégénérées ne font jamais crasher le scan.
/// Toute frontière externe dégrade proprement (résultat Unknown/vide), jamais d'exception.
/// </summary>
public class OldHardwareTests
{

    // Sonde reseau NEUTRE : la lecture echoue => SettingsState.NetworkAdapters reste null =>
    // aucun conseil reseau. Ces tests parlent d'autre chose, le reseau ne doit pas les polluer.
    private static INetworkAdapterReader NoNetwork()
    {
        var r = Substitute.For<INetworkAdapterReader>();
        r.Read().Returns(Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "non lu"));
        return r;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static HardwareInventory EmptyGpuInventory() => new(
        new CpuInfo("Intel Core i7-6700K", "GenuineIntel", 4, 8, false, 0),
        Array.Empty<GpuInfo>(),          // <── liste vide : risque IndexOutOfRange
        new[] { new MemoryModule("PN", "Corsair", 4800, 6000) },
        new MotherboardInfo("ASUS", "Z170"),
        new BiosInfo("1.0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static HardwareInventory EmptyMemoryInventory() => new(
        new CpuInfo("AMD Ryzen 5 3600", "AMD", 6, 12, false, 1),
        new[] { new GpuInfo("RX 6600", "AMD", 8L << 30, "23", TriState.On) },
        Array.Empty<MemoryModule>(),     // <── liste vide : risque dans XmpInferencer
        new MotherboardInfo("GIGABYTE", "B550"),
        new BiosInfo("F14", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static HardwareInventory FullyDegenerateInventory() => new(
        new CpuInfo("Unknown CPU", "Unknown", 0, 0, false, 0),
        Array.Empty<GpuInfo>(),
        Array.Empty<MemoryModule>(),
        new MotherboardInfo("Unknown", "Unknown"),
        new BiosInfo("0", null, false),
        ChassisType.Unknown);

    private static SettingsState AnyState() => new(
        TriState.Unknown, TriState.Unknown,
        "381b4222-f694-41f0-9685-ff5bb260df2e",
        TriState.Unknown, TriState.Off, Array.Empty<int>(), TriState.Unknown);

    // ── Gpus vide → SettingsStateReader ne lève pas, ReBar == Unknown ─

    [Fact]
    public async Task SettingsStateReader_empty_gpu_list_yields_rebar_unknown_not_crash()
    {
        // Cas limite : rebar = hw.Gpus[0].ReBar sans vérifier Count > 0
        // était potentiellement dangereux — guard déjà en place, ce test le verrouille.
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Ok("381b4222-f694-41f0-9685-ff5bb260df2e"));
        var reg = Substitute.For<IRegistryProvider>();
        reg.GetHags().Returns(TriState.Unknown);
        reg.GetGameMode().Returns(TriState.Unknown);
        reg.GetParkedCoreIndexes().Returns(Array.Empty<int>());
        reg.GetVrr().Returns(TriState.Unknown);

        var sut = new SettingsStateReader(power, reg, NoNetwork());
        var act = () => sut.ReadAsync(EmptyGpuInventory());

        await act.Should().NotThrowAsync("liste GPU vide → ReBar dégradé à Unknown, pas d'exception");

        var state = await sut.ReadAsync(EmptyGpuInventory());
        state.IsOk.Should().BeTrue();
        state.Value!.ReBar.Should().Be(TriState.Unknown, "aucun GPU détecté → Unknown honnête");
    }

    // ── Puce intégrée listée devant la carte → ReBar lu sur la carte ─

    [Fact]
    public async Task SettingsStateReader_lit_le_rebar_de_la_carte_dediee_pas_de_la_puce_integree()
    {
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Ok("381b4222-f694-41f0-9685-ff5bb260df2e"));
        var reg = Substitute.For<IRegistryProvider>();
        reg.GetParkedCoreIndexes().Returns(Array.Empty<int>());
        var hw = EmptyGpuInventory() with
        {
            Gpus = new[]
            {
                new GpuInfo("Intel(R) UHD Graphics 770", "Intel", 128L << 20, "31.0", TriState.Off),
                new GpuInfo("RTX 4060", "NVIDIA", 8L << 30, "616.56", TriState.On),
            },
        };

        var state = await new SettingsStateReader(power, reg, NoNetwork()).ReadAsync(hw);

        state.Value!.ReBar.Should().Be(TriState.On);
    }

    // ── Memory vide → XmpInferencer retourne Unknown, scorer ne crash pas ─

    [Fact]
    public void ScanScorer_empty_memory_list_no_memory_finding_no_crash()
    {
        var result = new ScanScorer().Score(EmptyMemoryInventory(), AnyState(), baseline: null);
        result.IsOk.Should().BeTrue("scorer ne lève jamais, même mémoire vide");
        result.Value!.Ledger!.Sum(e => e.Penalty).Should().BeInRange(0, 100);
        // Aucun finding Memory (XMP Unknown ne génère pas de défaut)
        result.Value.Findings.Should().NotContain(f => f.Category == "Memory",
            "XmpInferencer.Unknown = pas de finding mémoire, pas de blâme sans preuve");
    }

    // ── Inventaire complètement dégénéré → score valide, pas de crash ──

    [Fact]
    public void ScanScorer_fully_degenerate_inventory_returns_valid_result()
    {
        var hw    = FullyDegenerateInventory();
        var state = AnyState();
        var act   = () => new ScanScorer().Score(hw, state, baseline: null);

        act.Should().NotThrow("inventaire vide/inconnu ne lève jamais");
        var r = new ScanScorer().Score(hw, state, baseline: null);
        r.IsOk.Should().BeTrue();
        r.Value!.Ledger!.Sum(e => e.Penalty).Should().BeInRange(0, 100);
    }

    // ── CPU avec champs WMI absents → valeurs par défaut, pas d'exception ─

    [Fact]
    public void Ancient_cpu_with_missing_fields_yields_defaults_not_crash()
    {
        // CPU 2015 : certains champs WMI absents/null.
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["Name"] = "Intel Core i7-6700K" }) }));
        var r = new CpuDetector(q).Detect();
        r.IsOk.Should().BeTrue();
        r.Value!.Cores.Should().Be(0);    // champ absent → 0, pas d'exception
        r.Value.IsX3D.Should().BeFalse();
        r.Value.CcdCount.Should().Be(0);  // Intel
    }

    // ── Valeurs WMI garbage (mauvais type, chaîne non parsable) ────────

    [Fact]
    public void Garbage_wmi_values_do_not_throw()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow>
            {
                new(new Dictionary<string, object?>
                {
                    ["Name"]                       = "X",
                    ["NumberOfCores"]              = "not-a-number",
                    ["NumberOfLogicalProcessors"]  = new object()
                })
            }));
        new CpuDetector(q).Invoking(_ => _.Detect()).Should().NotThrow();
    }

    // ── ChassisType.Unknown → le scan se termine proprement ────────────

    [Fact]
    public void ChassisType_unknown_machine_scan_completes_without_crash()
    {
        // Vieille machine sans table SMBIOS standard : châssis inconnu.
        // Ne pas crasher, pas de finding BIOS/firmware imaginé.
        var hw    = FullyDegenerateInventory(); // ChassisType.Unknown
        var state = AnyState();
        var r     = new ScanScorer().Score(hw, state, baseline: null);

        r.IsOk.Should().BeTrue();
        r.Value!.Hw.Chassis.Should().Be(ChassisType.Unknown);
        // firmwareTweakable = false pour Unknown → pas de finding mémoire/GPU imaginés
        r.Value.Findings.Should().NotContain(f => f.Category == "Memory");
        r.Value.Findings.Should().NotContain(f => f.Category == "Gpu");
    }

    // ── ScoringRules avec GPU liste vide (scoreur direct) ──────────────

    [Fact]
    public void ScoringRules_empty_gpu_list_does_not_crash_and_produces_no_gpu_finding()
    {
        // ScoringRules.Evaluate accède à hw.Gpus[0] — guard Count > 0 doit tenir.
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 5 5600X", "AMD", 6, 12, false, 1),
            Array.Empty<GpuInfo>(),
            new[] { new MemoryModule("PN", "Samsung", 3600, 3600) },
            new MotherboardInfo("MSI", "B550"),
            new BiosInfo("1.0", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);
        var state = new SettingsState(TriState.On, TriState.Unknown,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

        var act = () => ScoringRules.Evaluate(hw, state);
        act.Should().NotThrow("liste GPU vide sans GPU finding → Unknown info seulement");

        var findings = ScoringRules.Evaluate(hw, state);
        // ReBar Unknown → Info finding (honest labelling) sur Desktop sans GPU
        // Mais la règle gère hw.Gpus.Count > 0 → Unknown quand vide
        findings.Should().NotContain(f => f.Category == "Gpu" && f.Severity == Severity.Minor,
            "pas de penalité GPU quand aucun GPU n'est détecté");
    }
}
