using DrDxt.Core;
using DrDxt.Detection.Scoring;
using DrDxt.Measurement;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>
/// T1.31 — Pipeline complet (fakes déterministes) → ScanResult cohérent.
/// Prouve que les briques se composent correctement : scorer reflète les findings,
/// les catégories attendues sont présentes, baseline est transportée, pénalités appliquées.
/// </summary>
public class ScanEndToEndTests
{
    [Fact]
    public void Misconfigured_machine_scores_below_90_with_named_categories_and_gain_no_fix()
    {
        // Machine mal réglée : XMP off + Balanced + HAGS off → findings réels, gain chiffré, aucun fix.
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 9 7950X3D", "AMD", 16, 32, true, 2),
            new[] { new GpuInfo("AMD Radeon RX 7900 XT", "AMD", 20L << 30, "1", TriState.Off) },
            new[]
            {
                // Deux barrettes correctement reparties : ce test porte sur XMP/HAGS/plan, pas sur
                // la topologie. Une seule barrette sur un fixe est desormais un constat a part
                // entiere (gratuit, sans fix logiciel) et ferait tomber le verrou du funnel.
                new MemoryModule("PN", "G.Skill", 4800, 6000, DeviceLocator: "Controller0-DIMMA2"),
                new MemoryModule("PN", "G.Skill", 4800, 6000, DeviceLocator: "Controller1-DIMMB2"),
            },
            new MotherboardInfo("ASUS", "X670E"),
            new BiosInfo("2", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);

        var state = new SettingsState(
            XmpExpo: TriState.Off,
            ReBar: TriState.Off,
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e", // Balanced
            Hags: TriState.Off,
            GameMode: TriState.On,
            ParkedCoreIndexes: Array.Empty<int>(),
            Vrr: TriState.Unknown);

        var baseline = new BenchmarkResult(110, 80, 60, new List<double> { 9.0, 9.1 }, null, 1.2, 5);

        var result = new ScanScorer().Score(hw, state, baseline);

        result.IsOk.Should().BeTrue();
        result.Value!.Score.Should().BeInRange(1, 89,
            "machine mal réglée : Memory(12) + Scheduling×2(12+4) + Latency(4) + Gpu(4) = -36pts → score 64");

        result.Value.Findings
            .Select(f => f.Category)
            .Distinct()
            .Should().Contain(new[] { "Memory", "Scheduling", "Latency", "Gpu" },
                "toutes les catégories détectées pour cette config");

        result.Value.Findings
            .Should().Contain(f => f.EstimatedGainPct > 0,
                "au moins un finding a un gain chiffré (XMP, Scheduling, Latency, GPU)");

    }

    [Fact]
    public void Scan_result_carries_hw_state_and_baseline_through()
    {
        // Vérifie que hw / state / baseline sont bien transportés dans ScanResult
        // sans transformation, mutation ou référence brisée.
        var hw = new HardwareInventory(
            new CpuInfo("Intel Core i9-14900K", "GenuineIntel", 24, 32, false, 0),
            new[] { new GpuInfo("NVIDIA RTX 4090", "NVIDIA", 24L << 30, "546", TriState.Unknown) },
            new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
            new MotherboardInfo("MSI", "Z790"),
            new BiosInfo("7D25vH9", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);

        var state = new SettingsState(
            TriState.On, TriState.Unknown,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", // High Performance
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

        var baseline = new BenchmarkResult(165, 140, 110, new List<double> { 6.0, 6.1 }, null, 0.8, 5);

        var r = new ScanScorer().Score(hw, state, baseline).Value!;

        r.Hw.Should().BeSameAs(hw,       "hw transporté tel quel");
        r.State.Should().BeSameAs(state, "state transporté tel quel");
        r.Baseline.Should().BeSameAs(baseline, "baseline transportée telle quelle");
    }

    [Fact]
    public void Well_configured_machine_scores_at_least_90()
    {
        // Machine bien réglée : pas de penalité majeure → score ≥ 90.
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
            new[] { new GpuInfo("AMD Radeon RX 7900 XTX", "AMD", 24L << 30, "23", TriState.On) },
            new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
            new MotherboardInfo("ASUS", "X670E"),
            new BiosInfo("2", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);

        var state = new SettingsState(
            XmpExpo: TriState.On,
            ReBar: TriState.On,
            PowerPlanGuid: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", // High Performance
            Hags: TriState.On,
            GameMode: TriState.On,
            ParkedCoreIndexes: Array.Empty<int>(),
            Vrr: TriState.Unknown);

        var baseline = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

        var r = new ScanScorer().Score(hw, state, baseline).Value!;
        r.Score.Should().BeGreaterThanOrEqualTo(90, "machine déjà bien réglée → pas de penalité majeure");
    }

    [Fact]
    public void Degraded_scan_null_baseline_still_produces_coherent_result()
    {
        // Mode dégradé : PresentMon absent → baseline null.
        // ScanResult doit rester cohérent : score borné, findings valides, baseline null.
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 5 5600X", "AMD", 6, 12, false, 1),
            new[] { new GpuInfo("NVIDIA RTX 3070", "NVIDIA", 8L << 30, "531", TriState.Unknown) },
            new[] { new MemoryModule("PN", "Corsair", 3200, 3600) },
            new MotherboardInfo("MSI", "B550"),
            new BiosInfo("7D51v1B", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);

        var state = new SettingsState(
            TriState.Off, TriState.Unknown,
            "381b4222-f694-41f0-9685-ff5bb260df2e",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

        var result = new ScanScorer().Score(hw, state, baseline: null);

        result.IsOk.Should().BeTrue();
        result.Value!.Baseline.Should().BeNull("mode dégradé = pas de baseline");
        result.Value.Score.Should().BeInRange(0, 100);
        result.Value.Findings.Should().NotBeNull();
    }

    [Fact]
    public void Ab_verdict_on_baseline_vs_itself_is_no_gain()
    {
        var b = new BenchmarkResult(120, 90, 70, new List<double> { 8.3, 8.3 }, null, 1.0, 5);
        AbComparer.Compare(b, b).IsMeasurableGain.Should().BeFalse(
            "comparer une baseline à elle-même = delta 0 ≤ marge → jamais un gain");
    }

    [Fact]
    public void Score_reflects_penalty_arithmetic_accurately()
    {
        // Modèle "ledger de déductions" (plan 11, corrigé par l'audit) : le global est le REGISTRE
        // PLAT = clamp(100 - Σ pénalités des leviers applicables/connus/sous-optimaux), pas une
        // moyenne pondérée 50/30/20 (§5 point 7).
        // Config : XMP off (Major 15) = seul levier réel ici. Balanced sans symptôme core-parking
        // → neutre (M5). ReBar On → Optimal. Pilote NVIDIA "531" ≥ floor 500 → frais. VRR Unknown → 0.
        var hw = new HardwareInventory(
            new CpuInfo("Intel Core i7-13700K", "GenuineIntel", 16, 24, false, 0),
            new[] { new GpuInfo("NVIDIA RTX 3080", "NVIDIA", 10L << 30, "531", TriState.On) },
            new[] { new MemoryModule("PN", "Kingston", 3200, 4800) },
            new MotherboardInfo("ASUS", "Z790"),
            new BiosInfo("1.0", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);

        var state = new SettingsState(
            XmpExpo: TriState.Off,     // Major penalty 15 (desktop)
            ReBar: TriState.On,
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",  // Balanced, sans symptôme → neutre
            Hags: TriState.On,
            GameMode: TriState.On,
            ParkedCoreIndexes: Array.Empty<int>(),
            Vrr: TriState.Unknown);

        var r = new ScanScorer().Score(hw, state, baseline: null).Value!;

        // Registre plat : 100 - 15 (XMP off) = 85. Les neutres (Balanced sans symptôme, HAGS, VRR)
        // ne fabriquent plus de déficit (audit C2/M5).
        r.Score.Should().Be(85, "registre plat 100 - Σ pénalités, neutres non pénalisés (audit C2/M5)");
    }
}
