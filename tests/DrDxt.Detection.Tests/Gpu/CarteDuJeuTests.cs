using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// La carte qui porte le jeu, d'après la mémoire graphique qu'il engage sur chacune.
public sealed class CarteDuJeuTests
{
    private const string Igpu = "luid_0x00000000_0x0000a001", Dgpu = "luid_0x00000000_0x0000b002";
    private static readonly DxgiAdapter[] Portable =
    [
        new("Intel(R) UHD Graphics", 0x8086, 128L << 20, Igpu),
        new("NVIDIA GeForce RTX 3060 Laptop GPU", 0x10de, 6L << 30, Dgpu),
    ];

    [Fact]
    public void Un_jeu_sur_la_puce_integree_nomme_la_vraie_carte_laissee_de_cote()
    {
        var lu = CarteDuJeu.Juger(42, Portable, [($"pid_42_{Igpu.ToUpperInvariant()}_phys_0", 900L << 20), ($"pid_42_{Dgpu}_phys_0", 20L << 20)]);
        lu.Should().Be(new CarteDuJeuLue("Intel(R) UHD Graphics", true, "NVIDIA GeForce RTX 3060 Laptop GPU"));
    }

    [Fact]
    public void Un_jeu_sur_la_vraie_carte_ne_dit_rien_d_autre_qu_elle()
        => CarteDuJeu.Juger(42, Portable, [($"pid_42_{Igpu}_phys_0", 30L << 20), ($"pid_42_{Dgpu}_phys_0", 3L << 30)])
            .Should().Be(new CarteDuJeuLue("NVIDIA GeForce RTX 3060 Laptop GPU", false, null));

    [Fact]
    public void Un_autre_processus_ou_un_identifiant_qui_n_est_pas_une_carte_ne_compte_pas()
    {
        CarteDuJeu.Juger(42, Portable, [($"pid_7_{Igpu}_phys_0", 900L << 20)]).Should().BeNull();
        CarteDuJeu.Juger(42, Portable, [("pid_42_luid_0x00000000_0x00011c66_phys_0", 900L << 20)]).Should().BeNull();
        // pid 42 ne doit pas prendre les compteurs du pid 421.
        CarteDuJeu.Juger(42, Portable, [($"pid_421_{Igpu}_phys_0", 900L << 20)]).Should().BeNull();
    }

    [Fact, Trait("Category", "Machine")] // lit les vrais compteurs et la vraie carte de cette machine
    public void Le_processus_d_affichage_de_la_machine_est_porte_par_sa_vraie_carte()
    {
        var dwm = System.Diagnostics.Process.GetProcessesByName("dwm").First();
        var lu = CarteDuJeu.Lire(dwm.Id);
        lu.Should().NotBeNull();
        lu!.Integree.Should().BeFalse();
        Console.WriteLine(lu);
    }
}
