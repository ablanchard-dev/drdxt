using DrDxt.Core;
using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// Les superpositions matérielles : la valeur absente est le défaut de Windows, jamais « non lu ».
public sealed class SuperpositionsMateriellesTests
{
    // Notre règle : absent = défaut, illisible = non lu. Les confondre dirait « non lu » sur presque toutes les machines.
    [Fact]
    public void Une_valeur_absente_est_le_defaut_et_une_cle_illisible_est_non_lue()
    {
        SuperpositionsMaterielles.Juger(true, null).Statut.Should().Be(RepairStatus.ExpectedDefault);
        SuperpositionsMaterielles.Juger(false, null).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void La_valeur_5_coupe_les_superpositions_et_appelle_un_geste()
    {
        var c = SuperpositionsMaterielles.Juger(true, 5);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Une_autre_valeur_est_dite_sans_verdict()
        => SuperpositionsMaterielles.Juger(true, 2).Statut.Should().Be(RepairStatus.Observed);
}
