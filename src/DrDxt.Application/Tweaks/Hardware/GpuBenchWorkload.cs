namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : la définition de la charge déterministe + sa réponse connue calculée par le CPU.
//
// La source unique de ce que calcule le banc. Le vrai compute shader D3D11 (Detection)
// et cette référence CPU appliquent la même récurrence entière : la somme de contrôle du
// GPU se vérifie contre une réponse connue calculée ici. Un écart = corruption GPU
// silencieuse = instable (« pas de TDR ≠ stable »).
//
// La récurrence est un simple mélange de hachage entier (xorshift + multiplication-addition)
// sans virgule flottante ni heure ni aléa : le résultat est exact au bit près et identique
// sur tout périphérique sain, à chaque passe. Chaque élément est replié sur `Iterations`
// tours, puis tous les éléments sont réduits en une somme de contrôle de 64 bits.
//
// La spec par défaut donne quelques secondes de charge bornée sur un GPU milieu de gamme,
// mais se calcule vite sur CPU pour la réponse connue (le CPU ne fait que reproduire le
// calcul, c'est le GPU qui subit la charge bornée).
// -----------------------------------------------------------------------
public static class GpuBenchWorkload
{
    /// <summary>La spec de charge fixe par défaut, utilisée par la sonde et l'outil de banc.
    /// Graine constante ⇒ réponse connue reproductible ; dimensionnée pour quelques secondes de charge GPU.</summary>
    public static readonly GpuWorkloadSpec Default = new(ElementCount: 1 << 20, Iterations: 256, Seed: 0x9E3779B1u);

    /// <summary>Une spec qui charge surtout la bande passante en écriture : un grand tampon de sortie
    /// (1&lt;&lt;25 = 128 Mo, bien au-delà des ~24 Mo de L2 de la 4060) avec un seul tour d'ALU par
    /// élément : la charge bornée est dominée par l'écriture du tampon en VRAM, pas par l'ALU. Même
    /// shader, même somme de contrôle déterministe. C'est la première sonde, peu coûteuse, pour savoir
    /// si le banc voit un changement d'horloge mémoire (les 4 Mo de travail de la spec Default tiennent
    /// dans la L2, il ne le voit donc pas). Si ceci ne bouge toujours pas avec le décalage mémoire,
    /// un noyau de bande passante en lecture se justifie.</summary>
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

    /// <summary>La récurrence déterministe par élément : depuis un départ par index (Seed + i),
    /// <paramref name="iterations" /> tours de MixRound. C'est la partie lourde que le GPU fait
    /// tourner dans le compute shader (sensible à l'horloge). Le shader HLSL calcule la même
    /// valeur pour chaque index d'élément.</summary>
    public static uint ComputeElement(uint seed, int index, int iterations)
    {
        uint state = unchecked(seed + (uint)index);
        for (int r = 0; r < iterations; r++)
            state = MixRound(state);
        return state;
    }

    /// <summary>La réduction déterministe, côté CPU, des résultats par élément en une somme de
    /// contrôle de 64 bits. Pondérée par la position et tournée, pour qu'un seul élément faux la
    /// change (sensible à l'ordre). Le vrai runner relit le tampon de sortie du GPU et le replie
    /// avec cette même fonction : GPU et CPU s'accordent.</summary>
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

    /// <summary>Calcule la somme de contrôle exacte sur 64 bits que le GPU doit produire pour une
    /// passe saine de <paramref name="spec" /> : la récurrence par élément puis le repli. C'est la
    /// réponse connue à laquelle la sonde compare (écart ⇒ Unstable).</summary>
    public static ulong ComputeKnownChecksum(GpuWorkloadSpec spec)
    {
        var elements = new uint[spec.ElementCount];
        for (int i = 0; i < spec.ElementCount; i++)
            elements[i] = ComputeElement(spec.Seed, i, spec.Iterations);
        return Fold(elements);
    }
}
