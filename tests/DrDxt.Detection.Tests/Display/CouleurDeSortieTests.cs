using DrDxt.Core;
using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// Ce que la carte envoie, écran par écran.
public sealed class CouleurDeSortieTests
{
    [Fact]
    public void Le_paquet_se_lit_aux_decalages_du_sdk()
    {
        var p = new byte[32];
        BitConverter.GetBytes(0b11u).CopyTo(p, 20);
        BitConverter.GetBytes(3u).CopyTo(p, 24);
        BitConverter.GetBytes(10u).CopyTo(p, 28);
        CouleurDeSortie.Decouper(p, "E").Should().Be(new CouleurDeSortie.Sortie("E", 3, 10, true));
    }

    // Chaque écran a sa propre sortie : une ligne par écran, chacune la sienne, jamais la valeur du premier pour tous.
    [Fact]
    public void Chaque_ecran_a_sa_propre_ligne()
    {
        var l = CouleurDeSortie.Juger([new("A", 0, 8, false), new("B", 3, 8, false)]).ToList();
        l.Should().HaveCount(2);
        l[0].Statut.Should().Be(RepairStatus.ExpectedDefault);
        l[1].Statut.Should().Be(RepairStatus.Observed);
        l[1].ValeurCourante.Should().Contain("4:2:0");
    }

    [Fact]
    public void Moins_de_8_bits_est_dit_et_non_lu_reste_inconnu()
    {
        CouleurDeSortie.Juger([new("A", 0, 6, false)]).Single().Statut.Should().Be(RepairStatus.Observed);
        CouleurDeSortie.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
}
