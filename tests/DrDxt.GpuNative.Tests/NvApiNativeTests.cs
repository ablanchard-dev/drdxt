using System.Runtime.InteropServices;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests.NvApi;

// -----------------------------------------------------------------------
// NvApiNative : les tests de gel et de contrat de l'unique frontière native NVAPI,
// placés ici avec le code testé. Ils vérifient la feuille.
//
//   • Disposition des structures : Marshal.SizeOf de chaque structure interop doit égaler la
//     taille exacte en octets de la structure C de NVIDIA (recoupée avec NvAPIWrapper). C'est
//     l'invariant porteur : MakeVersion<T>() = sizeof(T) | (ver << 16), donc une mauvaise
//     taille = le rejet sûr IncompatibleStructVersion du pilote (jamais une écriture
//     corrompue). Ces tests attrapent toute dérive accidentelle de disposition.
//   • Recherche P0/GRAPHICS : l'écriture du décalage cœur doit muter exactement l'horloge
//     P0/Graphics que la lecture a rendue, même quand elle n'est pas en [0][0].
//   • Règles d'or de l'entrée : MakeVersion (calculé, jamais codé en dur) et l'échec propre
//     d'IsAvailable / EnumPhysicalGpus sur une machine sans nvapi64.dll.
// -----------------------------------------------------------------------
public sealed class NvApiStructLayoutTests
{
    // Chaîne Pstates20 (NvAPIWrapper : PerformanceStates20*.cs, tout en Pack = 8).
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

    // Politiques de puissance privées (NvAPIWrapper : PrivatePowerPolicies*V1.cs, Pack = 8).
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
    // L'écriture du décalage cœur doit muter exactement l'horloge P0/Graphics que la lecture a
    // rendue, même quand elle n'est pas en [0][0]. Ceci fige la recherche d'index qu'utilise
    // l'écriture en place (une écriture bâtie sur une structure vide serait critique).
    [Fact]
    public void Finds_the_p0_graphics_clock_at_its_real_indices()
    {
        var info = BuildInfo(numPstates: 3, numClocks: 2);
        info.Pstates[0].StateId = 99;       // pas P0
        info.Pstates[1].StateId = 99;       // pas P0
        info.Pstates[2].StateId = 0;        // P0 à l'index de pstate 2
        info.Pstates[2].Clocks[0].DomainId = 7;   // pas graphics
        info.Pstates[2].Clocks[1].DomainId = 0;   // graphics à l'index d'horloge 1

        var found = NvApiNative.TryFindP0GraphicsClockIndex(info, out var p, out var c);

        found.Should().BeTrue();
        p.Should().Be(2);
        c.Should().Be(1);
    }

    [Fact]
    public void Returns_false_when_no_p0_graphics_clock_is_present()
    {
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 5;        // pas P0

        NvApiNative.TryFindP0GraphicsClockIndex(info, out _, out _).Should().BeFalse();
    }

    // ── Recherche P0/MEMORY (la vraie lecture du décalage mémoire) ────────────
    // La lecture du décalage mémoire doit trouver exactement l'horloge P0/MEMORY que la lecture a
    // rendue : PublicClockDomain MEMORY = 4 (NV_GPU_PUBLIC_CLOCK_ID, frère de GRAPHICS = 0 ;
    // NvAPIWrapper PublicClockId.Memory). Ce test de gel fige cet identifiant de domaine : toute
    // dérive vers une mauvaise constante le casse. Requis parce qu'ApplyNvidiaAsync lit le décalage
    // cœur et le décalage mémoire et fait échouer l'undervolt si la lecture mémoire échoue.

    [Fact]
    public void Finds_the_p0_memory_clock_at_its_real_indices()
    {
        var info = BuildInfo(numPstates: 3, numClocks: 2);
        info.Pstates[0].StateId = 99;       // pas P0
        info.Pstates[1].StateId = 99;       // pas P0
        info.Pstates[2].StateId = 0;        // P0 à l'index de pstate 2
        info.Pstates[2].Clocks[0].DomainId = 0;   // graphics, pas memory
        info.Pstates[2].Clocks[1].DomainId = 4;   // memory (PublicClockDomain MEMORY = 4) à l'index d'horloge 1

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
        info.Pstates[0].Clocks[0].DomainId = 0; // graphics seulement : aucun domaine mémoire

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Memory_finder_does_not_confuse_graphics_or_other_domains_with_memory()
    {
        // P0 avec une horloge GRAPHICS (0) et une horloge PROCESSOR (7) mais sans mémoire (4) →
        // la recherche mémoire ne doit pas prendre un autre domaine pour la mémoire.
        var info = BuildInfo(numPstates: 1, numClocks: 2);
        info.Pstates[0].StateId = 0;
        info.Pstates[0].Clocks[0].DomainId = 0;  // graphics
        info.Pstates[0].Clocks[1].DomainId = 7;  // processor, pas memory

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Memory_finder_honors_the_reported_counts_and_ignores_entries_beyond_them()
    {
        // Une horloge mémoire P0 physiquement présente mais au-delà de NumPstates doit être ignorée.
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 99;           // le seul pstate compté n'est pas P0
        info.Pstates[3].StateId = 0;            // P0 existe, mais à l'index 3 > NumPstates
        info.Pstates[3].Clocks[0].DomainId = 4; // memory, mais au-delà du compte annoncé

        NvApiNative.TryFindP0MemoryClockIndex(info, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Honors_the_reported_counts_and_ignores_entries_beyond_them()
    {
        // Une horloge graphique P0 physiquement présente mais au-delà de NumPstates doit être ignorée.
        var info = BuildInfo(numPstates: 1, numClocks: 1);
        info.Pstates[0].StateId = 99;       // le seul pstate compté n'est pas P0
        info.Pstates[3].StateId = 0;        // P0 existe, mais à l'index 3 > NumPstates
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
// NvApiEntry : les règles d'or de sécurité, testées unitairement.
//
// Règle n° 1 (mot de version) : MakeVersion<T>(v) = sizeof(T) | (v << 16), calculé à
// l'exécution, jamais codé en dur. Un écart de taille entre structure C# et C remonte alors
// comme le rejet sûr IncompatibleStructVersion du pilote, jamais une écriture corrompue.
//
// Échec propre : sur une machine sans nvapi64.dll (chaque runner CI ou poste ici), IsAvailable
// est faux et EnumPhysicalGpus rend une liste vide ; ne lève jamais. (Sur une vraie machine
// NVIDIA ils peuvent être vrai / non vide ; l'invariant est « ne lève jamais ».)
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
        // sizeof(TwoUints) = 8 ; numéro de version 1 → (1 << 16) | 8 = 0x00010008.
        var version = NvApiEntry.MakeVersion<TwoUints>(1);

        version.Should().Be((uint)(Marshal.SizeOf<TwoUints>() | (1 << 16)));
        (version & 0xFFFF).Should().Be(8, "the struct size lives in the low 16 bits");
        (version >> 16).Should().Be(1, "the version number lives in the high 16 bits");
    }

    [Fact]
    public void MakeVersion_is_not_hardcoded_size_tracks_the_struct()
    {
        // Deux tailles de structure différentes doivent donner deux mots de version différents :
        // preuve que la taille est calculée, pas une constante (règle d'or n° 1).
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

        // Ne lève jamais, que nvapi64 existe ou non.
        var available = false;
        var enumAct = () => available = entry.IsAvailable;
        enumAct.Should().NotThrow();

        IReadOnlyList<IntPtr> gpus = Array.Empty<IntPtr>();
        var act = () => gpus = entry.EnumPhysicalGpus();
        act.Should().NotThrow("the NVAPI entry boundary must fail-clean, never throw");

        // Sur un runner sans NVIDIA : indisponible ⇒ aucune poignée de carte.
        if (!available)
            gpus.Should().BeEmpty();
    }
}
