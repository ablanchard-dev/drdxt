using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// 07/10/2026 (chantier D4) : les tâches planifiées dont la dernière exécution tombe pendant une partie relevée.
public sealed class TachesEnPartieTests
{
    private static readonly DateTimeOffset J = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly PlageDeJeu Partie = new(J.AddHours(1).AddMinutes(29), J.AddHours(3).AddMinutes(11));
    private static TacheExecutee T(string nom, double heures) => new(@"\Microsoft\Windows\" + nom, J.AddHours(heures));

    [Fact]
    public void Non_lu_n_est_jamais_aucune()
        => TachesEnPartie.Juger(null, [Partie]).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Les_taches_lancees_pendant_une_partie_sont_nommees_avec_la_limite_dite()
    {
        var c = TachesEnPartie.Juger([T("A", 2), T("B", 0.5), T("C", 2.9)], [Partie]);
        c.ValeurCourante.Should().Be("2 pendant une partie");
        c.Explication.Should().Contain(@"\Microsoft\Windows\A").And.Contain(@"\Microsoft\Windows\C")
            .And.NotContain(@"\Microsoft\Windows\B").And.Contain("Seule la dernière exécution");
    }

    [Fact]
    public void Aucune_pendant_les_parties_et_sans_partie_rien_n_est_compare()
    {
        TachesEnPartie.Juger([T("B", 0.5)], [Partie]).Statut.Should().Be(RepairStatus.ExpectedDefault);
        TachesEnPartie.Juger([T("A", 2)], []).Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Au_dela_de_cinq_noms_le_reste_est_compte()
        => TachesEnPartie.Juger(Enumerable.Range(0, 7).Select(i => T("T" + i, 2 + i * 0.1)).ToList(), [Partie]).Explication
            .Should().Contain("et 2 autre(s)").And.Contain(@"\Microsoft\Windows\T2").And.NotContain(@"\Microsoft\Windows\T1 ");
}
