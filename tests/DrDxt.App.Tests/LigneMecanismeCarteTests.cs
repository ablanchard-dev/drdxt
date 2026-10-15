using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>La limite de puissance jugée par son mécanisme, sur les parties normales.</summary>
public sealed class LigneMecanismeCarteTests
{
    private static PartieRelevee P(MecanismeCarte? m) =>
        new("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 10, 150, 90, BottleneckState.GpuBound) { MecanismeCarte = m };

    [Fact]
    public void Bridee_un_peu_a_un_petit_ecart_est_sans_effet()
        => ScanViewModel.LigneMecanismeCarte([P(new(100, 20, 2715, 2730))], k => k).Should().Be("Parties.Mecanisme.SansEffet");

    [Fact]
    public void Bridee_souvent_a_un_grand_ecart_coute()
        => ScanViewModel.LigneMecanismeCarte([P(new(100, 60, 2500, 2730))], k => k).Should().Be("Parties.Mecanisme.Coute");

    [Fact]
    public void Trop_peu_de_lectures_ou_aucune_rien_n_est_dit()
    {
        ScanViewModel.LigneMecanismeCarte([P(new(30, 6, 2715, 2730))], k => k).Should().BeNull();
        ScanViewModel.LigneMecanismeCarte([P(null)], k => k).Should().BeNull();
    }

    // Seules les parties du même jeu et du même côté de l'essai que la dernière comptent.
    [Fact]
    public void Un_autre_jeu_ou_l_autre_cote_de_l_essai_ne_se_melent_pas()
    {
        var derniere = P(new(40, 8, 2715, 2730));
        var autreJeu = P(new(100, 90, 2400, 2730)) with { Jeu = "jeub.exe" };
        var autreCote = P(new(100, 90, 2400, 2730)) with { Essai = "gpu.power_limit:on" };
        ScanViewModel.LigneMecanismeCarte([derniere, autreJeu, autreCote], k => k).Should().BeNull("40 lectures seulement dans les mêmes conditions");
    }
}
