using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Hardware;

// 06/10/2026 (chantier B6 a) : les erreurs que le disque n'a pas pu corriger (MSFT_StorageReliabilityCounter,
// ReadErrorsUncorrected et WriteErrorsUncorrected). Dit seulement quand il y en a ; un compteur absent n'est pas zéro.
public sealed class ErreursDisqueTests
{
    private static IWmiQuery Wmi(Dictionary<string, object?> compteurs)
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.StartsWith("SELECT")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
            {
                new(new Dictionary<string, object?> { ["FriendlyName"] = "Disque A", ["ObjectId"] = "{1}\\X" }),
            }));
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.StartsWith("ASSOCIATORS")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { new(compteurs) }));
        return wmi;
    }

    [Fact]
    public void Les_erreurs_non_corrigees_se_lisent_en_lecture_et_en_ecriture()
        => new StorageWearReader(Wmi(new() { ["Wear"] = "3", ["ReadErrorsUncorrected"] = "2", ["WriteErrorsUncorrected"] = "1" }))
            .Lire().Value!.Single().ErreursNonCorrigees.Should().Be(3);

    [Fact]
    public void Des_compteurs_absents_ne_valent_pas_zero()
        => new StorageWearReader(Wmi(new() { ["Wear"] = "0" })).Lire().Value!.Single().ErreursNonCorrigees.Should().BeNull();

    [Fact]
    public void Un_disque_avec_des_erreurs_non_corrigees_est_signale()
    {
        var c = StorageWearReader.JugerErreurs(new UsureDisque("Samsung SSD 990 PRO 2TB", 0, 42, ErreursNonCorrigees: 3))!;

        c.Nom.Should().Be("erreurs non corrigées · Samsung SSD 990 PRO 2TB");
        c.ValeurCourante.Should().Be("3");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("copie");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Zero_erreur_ou_compteur_absent_rien_n_est_dit()
    {
        StorageWearReader.JugerErreurs(new UsureDisque("A", 0, 40, ErreursNonCorrigees: 0)).Should().BeNull();
        StorageWearReader.JugerErreurs(new UsureDisque("A", 0, 40)).Should().BeNull();
    }
}
