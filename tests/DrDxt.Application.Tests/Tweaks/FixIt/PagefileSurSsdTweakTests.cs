using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// Le fichier d'échange posé sur un disque dur rendu à la gestion de Windows. Doublures seulement.
public sealed class PagefileSurSsdTweakTests
{
    private const string Chemin = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState Etat(TriState aDeplacer) =>
        new SettingsState(TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On) with { FichierDEchangeADeplacer = aDeplacer };

    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i5-12400F", "GenuineIntel", 6, 12, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 3060", "NVIDIA", 12L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B660"), new BiosInfo("1.0", null, true), ChassisType.Desktop);

    private PagefileSurSsdTweak Build(string? lu)
    {
        _registry.ReadMultiString(RegistryHiveKind.LocalMachine, Chemin, "PagingFiles").Returns(Result<string?>.Ok(lu));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new PagefileSurSsdTweak(_registry, _elevated);
    }

    [Fact]
    public void Propose_seulement_quand_l_etat_lu_le_dit()
    {
        var t = Build(@"D:\pagefile.sys 0 0");
        t.AppliesTo(Hw(), Etat(TriState.On)).Should().BeTrue();
        t.AppliesTo(Hw(), Etat(TriState.Off)).Should().BeFalse();
        t.AppliesTo(Hw(), Etat(TriState.Unknown)).Should().BeFalse();
        t.EtatNonLu(Etat(TriState.Unknown)).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_rend_la_gestion_a_windows_et_annuler_remet_l_entree_d_avant()
    {
        var t = Build(@"D:\pagefile.sys 0 0");

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        var pose = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "PagingFiles", RegistryValueType.MultiString, @"?:\pagefile.sys");
        var avant = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "PagingFiles", RegistryValueType.MultiString, @"D:\pagefile.sys 0 0");
        await _elevated.Received(1).ExecuteAsync(pose, Arg.Any<CancellationToken>());
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(avant);

        await t.RevertAsync(r.Value);
        await _elevated.Received(1).ExecuteAsync(avant, Arg.Any<CancellationToken>());
        ((ITweak)t).TakesEffectAfterReboot.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"D:\pagefile.sys 4096 8192")] // une taille choisie à la main entre-temps
    [InlineData(@"?:\pagefile.sys")]            // déjà géré par Windows
    [InlineData("")]                            // désactivé
    public async Task Relu_juste_avant_hors_de_son_cas_rien_n_est_ecrit(string lu)
    {
        (await Build(lu).ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
