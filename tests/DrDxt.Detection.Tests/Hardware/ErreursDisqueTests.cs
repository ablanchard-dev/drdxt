using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Hardware;

// Les erreurs que le disque n'a pas pu corriger (MSFT_StorageReliabilityCounter,
// ReadErrorsUncorrected et WriteErrorsUncorrected). Dit seulement quand il y en a ; un compteur absent n'est pas zéro.
public sealed class ErreursDisqueTests
{
    private static IWmiQuery Wmi(Dictionary<string, object?> compteurs, object? sante = null)
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.StartsWith("SELECT")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
            {
                new(new Dictionary<string, object?> { ["FriendlyName"] = "Disque A", ["ObjectId"] = "{1}\\X", ["HealthStatus"] = sante }),
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
        c.Explication.Should().Contain("abîmée").And.NotContain("chkdsk");
        c.Remede.Should().StartWith("1) Copie").And.Contain("chkdsk");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Zero_erreur_ou_compteur_absent_rien_n_est_dit()
    {
        StorageWearReader.JugerErreurs(new UsureDisque("A", 0, 40, ErreursNonCorrigees: 0)).Should().BeNull();
        StorageWearReader.JugerErreurs(new UsureDisque("A", 0, 40)).Should().BeNull();
    }

    // Ce que Windows dit de la santé du disque (MSFT_PhysicalDisk.HealthStatus, un UInt16).
    [Fact]
    public void La_sante_dite_par_Windows_se_lit_et_se_juge()
    {
        new StorageWearReader(Wmi(new() { ["Wear"] = "0" }, sante: (ushort)2)).Lire().Value!.Single().SanteWindows.Should().Be(2);
        new StorageWearReader(Wmi(new() { ["Wear"] = "0" })).Lire().Value!.Single().SanteWindows.Should().BeNull();

        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 2))!.ValeurCourante.Should().Be("en mauvaise santé");
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 1))!.Explication.Should().Contain("avertissement");
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 1))!.Remede.Should().StartWith("1) Copie");
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 2))!.Remede.Should().StartWith("1) Copie").And.Contain("remplacer");
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 0)).Should().BeNull();
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40, SanteWindows: 5)).Should().BeNull("inconnu : rien d'inventé");
        StorageWearReader.JugerSante(new UsureDisque("A", 0, 40)).Should().BeNull();
    }
}
