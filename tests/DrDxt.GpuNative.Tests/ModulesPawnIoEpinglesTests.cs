using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

/// <summary>
/// La promesse « DrDXT n'installe aucun pilote » dit aussi ce qu'il charge dans PawnIO quand le joueur l'a installé : ses
/// seuls modules, embarqués et vérifiés contre une empreinte SHA-256 épinglée. Un module dont l'empreinte ne correspond
/// pas n'est jamais rendu au chargement.
/// </summary>
public sealed class ModulesPawnIoEpinglesTests
{
    [Theory]
    [InlineData("IntelMSR", PawnIoIntel.EmpreinteMsr)]
    [InlineData("IntelMCHBAR", PawnIoIntel.EmpreinteMchbar)]
    [InlineData("LpcIO", PawnIoSuperIo.EmpreinteLpcIo)]
    public void Chaque_module_embarque_porte_son_empreinte_epinglee(string nom, string empreinte)
        => PawnIoIntel.Module(nom, empreinte).Should().NotBeNull();

    [Fact]
    public void Un_module_a_l_empreinte_inattendue_n_est_jamais_rendu()
        => PawnIoIntel.Module("IntelMSR", new string('0', 64)).Should().BeNull();

    [Fact]
    public void Un_module_absent_de_DrDXT_n_est_jamais_rendu()
        => PawnIoIntel.Module("AmdFamily17", PawnIoIntel.EmpreinteMsr).Should().BeNull();
}
