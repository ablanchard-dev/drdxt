using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

// Le cas ISLC, dit seulement quand il est mesuré : mémoire libre sous 1 024 Mo ET à-coups.
public sealed class MemoireEnPartieTests
{
    private const ulong Mo = 1024 * 1024;
    private static readonly double[] Fluide = Enumerable.Repeat(7.0, 200).ToArray();
    private static readonly double[] ACoups = [.. Enumerable.Repeat(7.0, 200), 30, 35, 40];

    [Fact]
    public void Memoire_basse_et_a_coups_donnent_le_constat()
    {
        var c = MemoireEnPartie.Juger(600 * Mo, ACoups);
        c!.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("600 Mo").And.Contain("3 images").And.Contain("ne vide rien");
    }

    [Fact]
    public void Memoire_basse_sans_a_coups_ne_dit_rien() => MemoireEnPartie.Juger(600 * Mo, Fluide).Should().BeNull();

    [Fact]
    public void A_coups_avec_de_la_memoire_libre_ne_sont_pas_l_affaire_de_la_memoire() =>
        MemoireEnPartie.Juger(1024 * Mo, ACoups).Should().BeNull("le seuil d'ISLC est « sous 1 024 Mo »");

    [Fact]
    public void Rien_de_mesure_ne_dit_rien()
    {
        MemoireEnPartie.Juger(null, ACoups).Should().BeNull();
        MemoireEnPartie.Juger(600 * Mo, null).Should().BeNull();
    }

    [Fact]
    public void Un_a_coup_est_une_image_au_dela_du_double_de_la_mediane() =>
        MemoireEnPartie.ACoups([7, 7, 7, 14, 14.1]).Should().Be(1);
}
