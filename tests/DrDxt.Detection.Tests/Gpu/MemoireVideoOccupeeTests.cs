using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// 04/10/2026 : la mémoire vidéo occupée, carte par carte. Le compteur Windows nomme chaque carte par son identifiant
// (luid_…_phys_N) ; il est rattaché à la carte DXGI de même identifiant. Revue du 04/10 (P1) : une carte intégrée
// (128 à 512 Mo « dédiés ») frôle toujours 100 % alors que le jeu vit en mémoire partagée, elle ne compte pas.
public sealed class MemoireVideoOccupeeTests
{
    private const string Rtx = "luid_0x00000000_0x000108ff";
    private const string Igpu = "luid_0x00000000_0x00011d82";
    private static readonly DxgiAdapter[] Cartes =
        [new("RTX 4060", 0x10DE, 8L << 30, Rtx), new("Intel UHD", 0x8086, 512L << 20, Igpu)];

    [Fact]
    public void Chaque_compteur_compte_pour_sa_carte()
        => MemoireVideoOccupee.Pourcent([(Rtx + "_phys_0", 7800L << 20), (Igpu + "_phys_0", 500L << 20)], Cartes)!
            .Value.Should().BeApproximately(95.2, 0.1, "la carte intégrée pleine à 98 % n'est pas la mémoire du jeu");

    [Fact]
    public void Une_carte_integree_seule_ne_dit_rien()
        => MemoireVideoOccupee.Pourcent([(Igpu + "_phys_0", 500L << 20)], [Cartes[1]]).Should().BeNull();

    [Fact]
    public void Sans_carte_ni_compteur_rattache_rien_n_est_dit()
    {
        MemoireVideoOccupee.Pourcent([("luid_0x00000000_0x0000dead_phys_0", 1L << 30)], Cartes).Should().BeNull();
        MemoireVideoOccupee.Pourcent([], Cartes).Should().BeNull();
        MemoireVideoOccupee.Pourcent([(Rtx + "_phys_0", 1L << 30)], [new("RTX", 0x10DE, 8L << 30)]).Should().BeNull("sans identifiant, rien n'est rattaché");
    }

    [Fact]
    public void Lu_sur_cette_machine_le_chiffre_est_entre_0_et_100()
    {
        var lu = MemoireVideoOccupee.Lire();
        if (lu is { } v) v.Should().BeInRange(0, 100);
    }
}
