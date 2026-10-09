using System.Runtime.InteropServices;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests.NvApi;

// -----------------------------------------------------------------------
// NvApiNative — the freeze/contract tests for the SINGLE NVAPI native boundary,
// relocated here (with the code under test) from DrDxt.ElevatedHelper.Tests by the
// 02f/N1 extraction. They assert exactly what they did before, against the leaf.
//
//   • STRUCT LAYOUT — Marshal.SizeOf of every interop struct must equal the exact
//     byte size of NVIDIA's C struct (cross-checked vs NvAPIWrapper). This is the
//     load-bearing invariant: MakeVersion<T>() = sizeof(T) | (ver << 16), so a wrong
//     size = the driver's SAFE IncompatibleStructVersion rejection (never a corrupt
//     write). These tests catch any accidental layout drift.
//   • P0/GRAPHICS FINDER — the core-offset write must mutate the exact P0/Graphics
//     clock the GET returned, even when it is not at [0][0].
//   • entry golden rules — MakeVersion (computed, never hard-coded) + the fail-clean
//     IsAvailable / EnumPhysicalGpus on a box without nvapi64.dll.
// -----------------------------------------------------------------------
public sealed class NvApiStructLayoutTests
{
    // Pstates20 chain (NvAPIWrapper: PerformanceStates20*.cs, all Pack = 8).
    [Fact]
    public void ParameterDelta_is_12_bytes() =>
        Marshal.SizeOf<NvGpuPerfPstates20ParameterDelta>().Should().Be(12);

    [Fact]
    public void ClockEntry_is_44_bytes() =>
        Marshal.SizeOf<NvGpuPstate20ClockEntryV1>().Should().Be(44);

    [Fact]
    public void BaseVoltageEntry_is_24_bytes() =>
        Marshal.SizeOf<NvGpuPstate20BaseVoltageEntryV1>().Should().Be(24);

    [Fact]
    public void PerformanceState20_is_456_bytes() =>
        Marshal.SizeOf<NvGpuPerformanceState20>().Should().Be(456);

    [Fact]
    public void Pstates20InfoV1_is_7316_bytes() =>
        Marshal.SizeOf<NvGpuPerfPstates20InfoV1>().Should().Be(7316);

    // Private power policies (NvAPIWrapper: PrivatePowerPolicies*V1.cs, Pack = 8).
    [Fact]
    public void PowerStatusEntry_is_16_bytes() =>
        Marshal.SizeOf<NvGpuPowerPolicyStatusEntry>().Should().Be(16);

    [Fact]
    public void PowerStatusV1_is_72_bytes() =>
        Marshal.SizeOf<NvGpuClientPowerPoliciesStatusV1>().Should().Be(72);

    [Fact]
    public void PowerInfoEntry_is_44_bytes() =>
        Marshal.SizeOf<NvGpuPowerPolicyInfoEntry>().Should().Be(44);

    [Fact]
    public void PowerInfoV1_is_184_bytes() =>
        Marshal.SizeOf<NvGpuClientPowerPoliciesInfoV1>().Should().Be(184);
}

public sealed class NvApiFinderTests
{
    // The core-offset write must mutate the exact P0/Graphics clock the GET returned —
    // even when it is not at [0][0]. This pins the index finder the in-place write uses
    // (the fix for the red-team's CRITICAL "zero-built write" finding).
    [Fact]
    public void Finds_the_p0_graphics_clock_at_its_real_indices()
    {
        var info = BuildInfo(numPstates: 3, numClocks: 2);
        info.Pstates[0].StateId = 99;       // not P0
        info.Pstates[1].StateId = 99;       // not P0
        info.Pstates[2].StateId = 0;        // P0 at pstate index 2
        info.Pstates[2].Clocks[0].DomainId = 7;   // not graphics
        info.Pstates[2].Clocks[1].DomainId = 0;   // graphics at clock index 1

        var found = NvApiNative.TryFindP0GraphicsClockIndex(info, out var p, out var c);

        found.Should().BeTrue();
        p.Should().Be(2);
        c.Should().Be(1);
    }

    [Fact]
    public void Returns_false_when_no_p0_graphics_clock_is_present()
    {
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 5;        // not P0

        NvApiNative.TryFindP0GraphicsClockIndex(info, out _, out _).Should().BeFalse();
    }

    // ── P0/MEMORY finder (the real memory-offset read) ──────────────────────
    // The memory-offset read must locate the exact P0/MEMORY clock the GET returned —
    // PublicClockDomain MEMORY = 4 (NV_GPU_PUBLIC_CLOCK_ID sibling of GRAPHICS = 0;
    // NvAPIWrapper PublicClockId.Memory). This FREEZE-TEST pins that domain id: any
    // drift to a wrong constant breaks it. Required because ApplyNvidiaAsync reads both
    // the core AND the memory offset and fails the undervolt if the mem read fails.

    [Fact]
    public void Finds_the_p0_memory_clock_at_its_real_indices()
    {
        var info = BuildInfo(numPstates: 3, numClocks: 2);
        info.Pstates[0].StateId = 99;       // not P0
        info.Pstates[1].StateId = 99;       // not P0
        info.Pstates[2].StateId = 0;        // P0 at pstate index 2
        info.Pstates[2].Clocks[0].DomainId = 0;   // graphics, not memory
        info.Pstates[2].Clocks[1].DomainId = 4;   // memory (PublicClockDomain MEMORY = 4) at clock index 1

        var found = NvApiNative.TryFindP0MemoryClockIndex(info, out var p, out var c);

        found.Should().BeTrue();
        p.Should().Be(2);
        c.Should().Be(1);
    }

    [Fact]
    public void Memory_finder_returns_false_when_no_p0_memory_clock_is_present()
    {
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 0;            // P0
        info.Pstates[0].Clocks[0].DomainId = 0; // graphics only — no memory domain

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Memory_finder_does_not_confuse_graphics_or_other_domains_with_memory()
    {
        // P0 with a GRAPHICS clock (0) + a PROCESSOR clock (7) but NO memory (4) →
        // the memory finder must not mistake another domain for memory.
        var info = BuildInfo(numPstates: 1, numClocks: 2);
        info.Pstates[0].StateId = 0;
        info.Pstates[0].Clocks[0].DomainId = 0;  // graphics
        info.Pstates[0].Clocks[1].DomainId = 7;  // processor, not memory

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Memory_finder_honors_the_reported_counts_and_ignores_entries_beyond_them()
    {
        // A P0 memory clock physically present but BEYOND NumPstates must be ignored.
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 99;           // the only counted pstate is not P0
        info.Pstates[3].StateId = 0;            // P0 exists, but at index 3 > NumPstates
        info.Pstates[3].Clocks[0].DomainId = 4; // memory, but beyond the reported count

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Honors_the_reported_counts_and_ignores_entries_beyond_them()
    {
        // A P0 graphics clock physically present but BEYOND NumPstates must be ignored.
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 99;       // the only counted pstate is not P0
        info.Pstates[3].StateId = 0;        // P0 exists, but at index 3 > NumPstates
        info.Pstates[3].Clocks[0].DomainId = 0;

        NvApiNative.TryFindP0GraphicsClockIndex(info, out _, out _).Should().BeFalse();
    }

    private static NvGpuPerfPstates20InfoV1 BuildInfo(uint numPstates, uint numClocks)
    {
        var info = new NvGpuPerfPstates20InfoV1
        {
            NumPstates = numPstates,
            NumClocks = numClocks,
            Pstates = new NvGpuPerformanceState20[NvApiTuningConstants.MaxPstates],
        };
        for (var i = 0; i < info.Pstates.Length; i++)
        {
            info.Pstates[i] = new NvGpuPerformanceState20
            {
                StateId = 99,
                Clocks = new NvGpuPstate20ClockEntryV1[NvApiTuningConstants.MaxClocks],
                BaseVoltages = new NvGpuPstate20BaseVoltageEntryV1[NvApiTuningConstants.MaxBaseVoltages],
            };
        }
        return info;
    }
}

// -----------------------------------------------------------------------
// NvApiEntry — the golden safety rules, unit-tested (relocated with the code).
//
// Rule #1 (version word): MakeVersion<T>(v) = sizeof(T) | (v << 16), computed at
// runtime — never hard-coded. A C#/C struct-size mismatch then surfaces as the
// driver's SAFE IncompatibleStructVersion, never a corrupt write.
//
// Fail-clean: on a box without nvapi64.dll (every CI/dev runner here), IsAvailable
// is false and EnumPhysicalGpus returns an empty list — never throws. (On a real
// NVIDIA box those may be true / non-empty; the invariant is "never throws".)
// -----------------------------------------------------------------------
public sealed class NvApiEntryTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TwoUints
    {
        public uint A;
        public uint B;
    }

    [Fact]
    public void MakeVersion_packs_size_in_low_word_and_version_in_high_word()
    {
        // sizeof(TwoUints) = 8; version number 1 → (1 << 16) | 8 = 0x00010008.
        var version = NvApiEntry.MakeVersion<TwoUints>(1);

        version.Should().Be((uint)(Marshal.SizeOf<TwoUints>() | (1 << 16)));
        (version & 0xFFFF).Should().Be(8, "the struct size lives in the low 16 bits");
        (version >> 16).Should().Be(1, "the version number lives in the high 16 bits");
    }

    [Fact]
    public void MakeVersion_is_not_hardcoded_size_tracks_the_struct()
    {
        // Two different struct sizes must yield two different version words — proving
        // the size is computed, not a constant (golden rule #1).
        var v1 = NvApiEntry.MakeVersion<TwoUints>(1);
        var v2 = NvApiEntry.MakeVersion<OneUint>(1);
        v1.Should().NotBe(v2);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct OneUint
    {
        public uint A;
    }

    [Fact]
    public void IsAvailable_and_EnumPhysicalGpus_fail_clean_without_nvapi()
    {
        var entry = new NvApiEntry();

        // Never throws regardless of whether nvapi64 exists.
        var available = false;
        var enumAct = () => available = entry.IsAvailable;
        enumAct.Should().NotThrow();

        IReadOnlyList<IntPtr> gpus = Array.Empty<IntPtr>();
        var act = () => gpus = entry.EnumPhysicalGpus();
        act.Should().NotThrow("the NVAPI entry boundary must fail-clean, never throw");

        // On a no-NVIDIA runner: not available ⇒ no GPU handles.
        if (!available)
            gpus.Should().BeEmpty();
    }
}
