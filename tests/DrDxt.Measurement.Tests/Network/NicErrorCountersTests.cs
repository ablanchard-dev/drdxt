using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

/// <summary>
/// Les compteurs d'une carte reseau sont des TOTAUX depuis le demarrage de la machine. Pris
/// seuls ils ne disent rien de l'instant present : mesure sur une machine reelle, 347 paquets
/// ignores sur 57 789 905 depuis le boot -- un chiffre qu'aucun diagnostic ne peut utiliser.
/// Le seul usage honnete est la DIFFERENCE entre deux instants, et c'est ce qu'on teste ici.
///
/// Complementaire du ping, pas redondant : le ping mesure la ROUTE (NetworkLatencyMeter),
/// ces compteurs mesurent la CARTE. Un lien peut pinguer parfaitement en jetant des trames.
/// </summary>
public sealed class NicErrorCountersTests
{
    private static NicCounterSnapshot Snap(
        long rxDisc = 0, long rxErr = 0, long txDisc = 0, long txErr = 0,
        long rxPkts = 0, long txPkts = 0)
        => new(rxDisc, rxErr, txDisc, txErr, rxPkts, txPkts);

    [Fact]
    public void Diff_SubtractsBothEnds_SoABootLongTotalNeverLeaksIn()
    {
        // Les chiffres reels d'une machine saine : 347 ignores depuis le boot, ZERO pendant la
        // fenetre. Le verdict doit etre propre -- sinon on accuserait une carte sur son passe.
        var before = Snap(rxDisc: 347, rxPkts: 57_789_905);
        var after = Snap(rxDisc: 347, rxPkts: 57_791_030);

        var d = NicErrorCounters.Diff(before, after);

        d.ReceivedDiscarded.Should().Be(0);
        d.ReceivedPackets.Should().Be(1_125);
        d.IsClean.Should().BeTrue("aucune erreur ni paquet ignore PENDANT la fenetre");
    }

    [Fact]
    public void Diff_CountsOnlyWhatHappenedInsideTheWindow()
    {
        var before = Snap(rxDisc: 100, rxErr: 5, txDisc: 2, txErr: 1, rxPkts: 1_000, txPkts: 900);
        var after = Snap(rxDisc: 112, rxErr: 5, txDisc: 2, txErr: 4, rxPkts: 3_000, txPkts: 2_900);

        var d = NicErrorCounters.Diff(before, after);

        d.ReceivedDiscarded.Should().Be(12);
        d.ReceivedErrors.Should().Be(0, "le total n'a pas bouge : rien de neuf a signaler");
        d.SentErrors.Should().Be(3);
        d.IsClean.Should().BeFalse();
    }

    [Fact]
    public void Diff_CounterWentBackwards_IsFlaggedAsReset_NotAsGoodNews()
    {
        // Un compteur qui recule = la carte a ete reinitialisee pendant la fenetre (driver
        // recharge, adaptateur redemarre). Un delta negatif n'a aucun sens, et le ramener a
        // zero fabriquerait un « tout va bien » a partir d'une perte d'information.
        var before = Snap(rxDisc: 500, rxPkts: 10_000);
        var after = Snap(rxDisc: 3, rxPkts: 40);

        var d = NicErrorCounters.Diff(before, after);

        d.CounterReset.Should().BeTrue();
        d.IsClean.Should().BeFalse("on ne sait plus ce qui s'est passe : ce n'est pas « propre »");
        d.ReceivedDiscarded.Should().BeGreaterThanOrEqualTo(0, "jamais un compte negatif");
        d.ReceivedDiscardPercent.Should().BeNull("aucun taux sur un compteur remis a zero");
    }

    [Fact]
    public void ReceivedDiscardPercent_IsNull_WhenNoPacketMovedInTheWindow()
    {
        var d = NicErrorCounters.Diff(Snap(), Snap());

        d.ReceivedDiscardPercent.Should().BeNull("zero paquet = rien a rapporter, jamais « 0 % »");
    }

    [Fact]
    public void ReceivedDiscardPercent_IsTheShareOfTheWindow_NotOfAllTime()
    {
        // 5 ignores sur 1 000 recus dans la fenetre = 0,5 %, quel que soit le passe de la carte.
        var d = NicErrorCounters.Diff(Snap(rxDisc: 9_000, rxPkts: 1_000_000),
                                      Snap(rxDisc: 9_005, rxPkts: 1_001_000));

        d.ReceivedDiscardPercent.Should().BeApproximately(0.5, 0.001);
    }
}
