using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// La liste des cœurs rapides, partagée par le banc processeur et le relevé des parties, sur
// d'autres processeurs que celui de la machine de référence (i7-14700F, 8 cœurs P en hyperthreading + 12 cœurs E).
public sealed class CoeursRapidesTests
{
    private static LogicalProcessor L(byte logique, byte coeur, byte classe, ushort groupe = 0)
        => new(logique, groupe, logique, coeur, classe, false);

    [Fact]
    public void Hybride_un_processeur_logique_par_coeur_de_la_classe_haute()
        => CpuTopology.CoeursRapides([L(0, 0, 1), L(1, 0, 1), L(2, 1, 1), L(3, 1, 1), L(4, 2, 0), L(5, 3, 0)])
            .Should().Equal(0, 2);

    [Fact]
    public void Homogene_type_Ryzen_tous_les_coeurs_comptent_une_fois()
        => CpuTopology.CoeursRapides([L(0, 0, 0), L(1, 0, 0), L(2, 1, 0), L(3, 1, 0), L(4, 2, 0), L(5, 2, 0)])
            .Should().Equal(0, 2, 4);

    [Fact]
    public void Hors_du_groupe_0_et_sans_topologie_rien_n_est_suppose()
    {
        CpuTopology.CoeursRapides([L(0, 0, 0), L(1, 1, 0, groupe: 1)]).Should().Equal(0);
        CpuTopology.CoeursRapides([]).Should().BeEmpty();
    }
}
