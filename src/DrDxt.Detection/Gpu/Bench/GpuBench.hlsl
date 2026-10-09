// -----------------------------------------------------------------------
// R2 GPU bench — DETERMINISTIC compute workload.
//
// One thread per element. Each thread runs the same integer recurrence as the CPU
// reference GpuBenchWorkload.ComputeElement / MixRound (xorshift32 + multiply-add),
// for a fixed number of iterations, then writes its final 32-bit state to the output
// buffer. The CPU reads the buffer back and folds it (GpuBenchWorkload.Fold) into the
// 64-bit checksum, which is compared against the known answer. Bit-exact, no floats,
// no RNG -> identical on every healthy device and every run.
//
// Iterations is passed as a constant so the same compiled shader serves any spec.
//
// Regenerate the embedded bytecode after editing this file:
//   fxc /T cs_5_0 /E CSMain /O3 /Fo GpuBench.cso GpuBench.hlsl
// (cs_5_0 = Shader Model 5.0, available on Feature Level 11 — RTX 4060 and most
//  2015+ GPUs). The .cso is embedded as a resource; see VorticeGpuComputeRunner.
// -----------------------------------------------------------------------

RWStructuredBuffer<uint> Output : register(u0);

cbuffer Params : register(b0)
{
    uint Seed;
    uint Iterations;
    uint ElementCount;
    uint _pad;
};

uint MixRound(uint state)
{
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    state = state * 0x85EBCA6Bu + 0xC2B2AE35u;
    return state;
}

[numthreads(256, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= ElementCount)
        return;

    uint state = Seed + i;
    for (uint r = 0; r < Iterations; r++)
        state = MixRound(state);

    Output[i] = state;
}
