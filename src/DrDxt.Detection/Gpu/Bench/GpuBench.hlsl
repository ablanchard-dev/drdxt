// -----------------------------------------------------------------------
// Banc GPU : charge de calcul déterministe.
//
// Un thread par élément. Chaque thread applique la même récurrence entière que la référence
// CPU GpuBenchWorkload.ComputeElement / MixRound (xorshift32 + multiplication-addition),
// pendant un nombre fixe d'itérations, puis écrit son état final sur 32 bits dans le tampon
// de sortie. Le CPU relit le tampon et le replie (GpuBenchWorkload.Fold) en somme de contrôle
// de 64 bits, comparée à la réponse connue. Exact au bit près, sans flottants, sans aléa ->
// identique sur tout périphérique sain, à chaque passe.
//
// Iterations est passé en constante : le même shader compilé sert toute spec.
//
// Régénérer le bytecode embarqué après toute modification de ce fichier :
//   fxc /T cs_5_0 /E CSMain /O3 /Fo GpuBench.cso GpuBench.hlsl
// (cs_5_0 = Shader Model 5.0, disponible au Feature Level 11 : RTX 4060 et la plupart des
//  GPU depuis 2015). Le .cso est embarqué comme ressource ; voir VorticeGpuComputeRunner.
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
