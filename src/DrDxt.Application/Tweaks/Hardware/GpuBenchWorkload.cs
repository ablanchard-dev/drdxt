namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — the DETERMINISTIC workload definition + its CPU known-answer.
//
// This is the single source of truth for what the bench computes. The real D3D11
// compute shader (Detection) and this CPU reference implement the same integer
// recurrence, so the GPU's output checksum can be verified against a known answer
// computed here. A mismatch = silent GPU corruption = unstable ("no TDR ≠ stable").
//
// The recurrence is a plain integer hash mix (xorshift + multiply-add) with NO
// floating point and NO time/RNG — so the result is bit-exact and identical on every
// healthy device and every run. Each element is folded over `Iterations` rounds, then
// all elements are reduced into one 64-bit checksum.
//
// Default spec is sized to be a few-second bounded load on a mid-range GPU yet finish
// quickly on CPU for the known-answer (the CPU side only needs to reproduce the math,
// it is the GPU that does the bounded stress).
// -----------------------------------------------------------------------
public static class GpuBenchWorkload
{
    /// <summary>The fixed default workload spec used by the probe and the bench tool.
    /// Constant seed ⇒ reproducible known-answer; sized for a multi-second GPU load.</summary>
    public static readonly GpuWorkloadSpec Default = new(ElementCount: 1 << 20, Iterations: 256, Seed: 0x9E3779B1u);

    /// <summary>A write-bandwidth-leaning spec: a big output buffer (1&lt;&lt;25 = 128 MB, far
    /// beyond the 4060's ~24 MB L2) with only 1 ALU round per element, so the bounded load is
    /// dominated by streaming the buffer out to VRAM rather than by core ALU. Same shader / same
    /// deterministic checksum. This is the cheap first probe for whether the bench can SEE a
    /// memory-clock change (the Default spec's 4 MB working set is L2-resident, so it can't). If
    /// this still doesn't move with the memory offset, a read-bandwidth kernel is warranted.</summary>
    public static readonly GpuWorkloadSpec BandwidthWrite = new(ElementCount: 1 << 25, Iterations: 1, Seed: 0x9E3779B1u);

    /// <summary>Per-element deterministic mix for one round. Pure integer ops; matches
    /// the HLSL compute shader (GpuBench.hlsl) exactly. Used by the CPU known-answer.</summary>
    private static uint MixRound(uint state)
    {
        // xorshift32 + a multiply-add avalanche — cheap, deterministic, no FP.
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        unchecked { state = state * 0x85EBCA6Bu + 0xC2B2AE35u; }
        return state;
    }

    /// <summary>The deterministic per-element recurrence: from a per-index start
    /// (Seed + i), run <paramref name="iterations" /> rounds of MixRound. This is the
    /// HEAVY part the GPU runs in the compute shader (clock-sensitive). The HLSL shader
    /// computes the IDENTICAL value for each element index.</summary>
    public static uint ComputeElement(uint seed, int index, int iterations)
    {
        uint state = unchecked(seed + (uint)index);
        for (int r = 0; r < iterations; r++)
            state = MixRound(state);
        return state;
    }

    /// <summary>The deterministic CPU reduction of the per-element results into one
    /// 64-bit checksum. Position-weighted + rotated so a single wrong element changes
    /// the checksum (order-sensitive). The real runner reads back the GPU's per-element
    /// output buffer and folds it with this exact function, so GPU and CPU agree.</summary>
    public static ulong Fold(ReadOnlySpan<uint> elements)
    {
        ulong checksum = 0;
        for (int i = 0; i < elements.Length; i++)
            unchecked
            {
                checksum += (ulong)elements[i] * (uint)(i + 1);
                checksum ^= (checksum << 7) | (checksum >> 57);
            }
        return checksum;
    }

    /// <summary>Computes the exact 64-bit checksum the GPU must produce for a healthy run
    /// of <paramref name="spec" /> — the composition of the per-element recurrence and the
    /// fold. This is the known-answer the probe compares against (mismatch ⇒ Unstable).</summary>
    public static ulong ComputeKnownChecksum(GpuWorkloadSpec spec)
    {
        var elements = new uint[spec.ElementCount];
        for (int i = 0; i < spec.ElementCount; i++)
            elements[i] = ComputeElement(spec.Seed, i, spec.Iterations);
        return Fold(elements);
    }
}
