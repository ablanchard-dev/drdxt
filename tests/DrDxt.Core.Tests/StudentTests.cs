using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Une seule table de Student dans le produit : des copies divergeraient (l'une s'arretant a 9 degres
/// de liberte, toutes retombant sur 1,96 au-dela, une marge trop etroite, jusqu'a 13,7 % a 10 degres). Une marge trop
/// etroite annonce un gain que la mesure ne prouve pas.
/// Valeurs de reference : t(0,975 ; ddl), scipy.stats.t.ppf.
/// </summary>
public sealed class StudentTests
{
    [Theory]
    [InlineData(1, 12.706)]
    [InlineData(9, 2.262)]
    [InlineData(10, 2.228)]
    [InlineData(20, 2.086)]
    public void La_table_rend_la_valeur_de_reference(int ddl, double t)
        => Student.T95(ddl).Should().BeApproximately(t, 0.001);

    [Theory]
    [InlineData(21, 2.0796)]
    [InlineData(30, 2.0423)]
    [InlineData(120, 1.9799)]
    [InlineData(100000, 1.9600)]
    public void Au_dela_de_la_table_la_marge_n_est_jamais_plus_etroite_que_la_vraie(int ddl, double vraie)
        => Student.T95(ddl).Should().BeGreaterThanOrEqualTo(vraie);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sans_degre_de_liberte_aucune_marge_n_est_possible(int ddl)
        => Student.T95(ddl).Should().Be(double.PositiveInfinity);

    [Fact]
    public void La_valeur_ne_croit_jamais_avec_les_degres_de_liberte()
    {
        var valeurs = Enumerable.Range(1, 60).Select(Student.T95).ToList();
        valeurs.Should().BeInDescendingOrder();
    }
}
