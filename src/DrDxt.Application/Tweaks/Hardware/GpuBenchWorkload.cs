namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : la charge déterministe et sa réponse connue, calculée par le CPU. Le compute shader D3D11 (Detection) et
// cette référence appliquent la même récurrence entière (xorshift + multiplication-addition, sans flottant, heure ni
// aléa : exacte au bit près sur tout périphérique sain), `Iterations` tours par élément puis une somme de contrôle de 64
// bits. Un écart = corruption GPU silencieuse = instable (« pas de TDR ≠ stable »). La spec par défaut charge un GPU
// milieu de gamme quelques secondes et se calcule vite sur CPU.
// -----------------------------------------------------------------------
public static class GpuBenchWorkload
{
    /// <summary>La spec de charge fixe par défaut, utilisée par la sonde et l'outil de banc.
    /// Graine constante ⇒ réponse connue reproductible ; dimensionnée pour quelques secondes de charge GPU.</summary>
    public static readonly GpuWorkloadSpec Default = new(ElementCount: 1 << 20, Iterations: 256, Seed: 0x9E3779B1u);

    /// <summary>La bande passante en écriture : un tampon de 1&lt;&lt;25 = 128 Mo (bien au-delà des ~24 Mo de L2 de la
    /// 4060), un seul tour d'ALU par élément, même shader. Les 4 Mo de la spec Default tiennent dans la L2 et ne voient
    /// pas l'horloge mémoire ; si celle-ci ne bouge pas non plus avec le décalage mémoire, un noyau en lecture se
    /// justifie.</summary>
    public static readonly GpuWorkloadSpec BandwidthWrite = new(ElementCount: 1 << 25, Iterations: 1, Seed: 0x9E3779B1u);

    /// <summary>Mélange déterministe d'un élément pour un tour. Opérations entières pures ;
    /// identique au compute shader HLSL (GpuBench.hlsl). Sert à la réponse connue du CPU.</summary>
    private static uint MixRound(uint state)
    {
        // xorshift32 + une avalanche multiplication-addition : peu coûteux, déterministe, sans virgule flottante.
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        unchecked { state = state * 0x85EBCA6Bu + 0xC2B2AE35u; }
        return state;
    }

    /// <summary>La récurrence par élément : depuis Seed + i, <paramref name="iterations" /> tours de MixRound, la partie
    /// lourde (sensible à l'horloge) que le shader HLSL calcule à l'identique.</summary>
    public static uint ComputeElement(uint seed, int index, int iterations)
    {
        uint state = unchecked(seed + (uint)index);
        for (int r = 0; r < iterations; r++)
            state = MixRound(state);
        return state;
    }

    /// <summary>La réduction en somme de contrôle de 64 bits, pondérée par la position et tournée : un seul élément faux
    /// la change. Le runner replie le tampon du GPU avec cette même fonction.</summary>
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

    /// <summary>La somme de contrôle qu'une passe saine de <paramref name="spec" /> doit produire : la réponse connue de
    /// la sonde (écart ⇒ Unstable).</summary>
    public static ulong ComputeKnownChecksum(GpuWorkloadSpec spec)
    {
        var elements = new uint[spec.ElementCount];
        for (int i = 0; i < spec.ElementCount; i++)
            elements[i] = ComputeElement(spec.Seed, i, spec.Iterations);
        return Fold(elements);
    }
}
