using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Hardware;

public sealed class UsureBatterieTests
{
    [Fact]
    public void Sous_80_pour_cent_de_la_capacite_d_origine_la_batterie_est_usee()
    {
        var c = UsureBatterie.Juger(60_000, 42_000).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("70 % de sa capacité d'origine");

        UsureBatterie.Juger(60_000, 55_000).Single().Statut.Should().Be(RepairStatus.Observed);
        UsureBatterie.Juger(null, 55_000).Should().BeEmpty();
        UsureBatterie.Juger(60_000, 0).Should().BeEmpty();
    }

    [Fact]
    public void Un_PC_fixe_sans_batterie_ne_dit_rien_et_deux_batteries_s_additionnent()
    {
        var sans = Substitute.For<IWmiQuery>();
        sans.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "absent"));
        sans.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "classe absente"));
        UsureBatterie.LireEtJuger(sans).Should().BeEmpty();

        var deux = Substitute.For<IWmiQuery>();
        deux.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "absent"));
        deux.Query(Arg.Is<string>(q => q.Contains("Win32_SystemEnclosure")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { new(new Dictionary<string, object?> { ["ChassisTypes"] = new ushort[] { 10 } }) }));
        deux.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryStaticData")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
                { new(new Dictionary<string, object?> { ["DesignedCapacity"] = (uint)40_000 }), new(new Dictionary<string, object?> { ["DesignedCapacity"] = (uint)20_000 }) }));
        deux.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryFullChargedCapacity")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
                { new(new Dictionary<string, object?> { ["FullChargedCapacity"] = (uint)30_000 }), new(new Dictionary<string, object?> { ["FullChargedCapacity"] = (uint)15_000 }) }));
        UsureBatterie.LireEtJuger(deux).Single().ValeurCourante.Should().Be("75 % de sa capacité d'origine");
    }

    // Un onduleur USB (périphérique HID d'alimentation) se publie comme une batterie ; sur un
    // boîtier de PC fixe (SMBIOS, Win32_SystemEnclosure.ChassisTypes 3 « Desktop »), rien n'est jugé.
    [Fact]
    public void Sur_un_PC_fixe_un_onduleur_n_est_pas_une_batterie()
    {
        var fixe = Substitute.For<IWmiQuery>();
        fixe.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "absent"));
        fixe.Query(Arg.Is<string>(q => q.Contains("Win32_SystemEnclosure")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { new(new Dictionary<string, object?> { ["ChassisTypes"] = new ushort[] { 3 } }) }));
        fixe.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryStaticData")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { new(new Dictionary<string, object?> { ["DesignedCapacity"] = (uint)40_000 }) }));
        fixe.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryFullChargedCapacity")))
            .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { new(new Dictionary<string, object?> { ["FullChargedCapacity"] = (uint)20_000 }) }));
        UsureBatterie.LireEtJuger(fixe).Should().BeEmpty();
    }

    // Le boîtier était relu ici sous la seule forme ushort[] avec sa propre liste de portables, alors que
    // ChassisDetector (int[], tableaux génériques, consoles portables) fait foi pour le reste de DrDXT : un fournisseur WMI qui
    // rend int[] faisait taire l'usure sur un vrai portable. Une seule détection.
    [Fact]
    public void Le_boitier_vient_de_la_detection_commune_quelle_que_soit_la_forme_rendue()
    {
        UsureBatterie.LireEtJuger(Wmi(new int[] { 10 }, "Model")).Single().ValeurCourante.Should().Be("50 % de sa capacité d'origine");
        UsureBatterie.LireEtJuger(Wmi(new ushort[] { 3 }, "ROG Ally RC71L")).Should().NotBeEmpty("une console portable a une batterie");
        UsureBatterie.LireEtJuger(Wmi(new ushort[] { 3 }, "MS-7D98")).Should().BeEmpty("un PC fixe : un onduleur n'est pas une batterie");
    }

    private static IWmiQuery Wmi(object chassis, string modele)
    {
        var w = Substitute.For<IWmiQuery>();
        IReadOnlyList<WmiRow> Ligne(string champ, object? v) => new List<WmiRow> { new(new Dictionary<string, object?> { [champ] = v }) };
        w.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "absent"));
        w.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.StorageFail, "absent"));
        w.Query(Arg.Is<string>(q => q.Contains("Win32_SystemEnclosure"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(Ligne("ChassisTypes", chassis)));
        w.Query(Arg.Is<string>(q => q.Contains("Win32_ComputerSystem"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(Ligne("Model", modele)));
        w.Query(Arg.Is<string>(q => q.Contains("Win32_SystemEnclosure"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(Ligne("ChassisTypes", chassis)));
        w.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryStaticData"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(Ligne("DesignedCapacity", (uint)40_000)));
        w.QueryInNamespace(Arg.Any<string>(), Arg.Is<string>(q => q.Contains("BatteryFullChargedCapacity"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(Ligne("FullChargedCapacity", (uint)20_000)));
        return w;
    }
}
