using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// Le profil GLOBAL du pilote portait « performances maximales »
// (lu le 24/09 : 55,6 W et 2 490 MHz au repos). Le levier le rend à « Normal » au global seulement ; l'original est
// lu avant l'écriture et l'annulation le réécrit tel quel. La relecture passe par la cible gardée dans le snapshot.
public sealed class ReposRenduALaCarteTweakTests
{
    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState MaxAuGlobal = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        NvidiaPreferMaxPerf: TriState.On, NvidiaDrsScope: null);

    private static HardwareInventory Machine(ChassisType chassis = ChassisType.Desktop, string vendor = "NVIDIA") => new(
        new CpuInfo("Intel(R) Core(TM) i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", vendor, 8L << 30, "581.29", TriState.On) },
        new[] { new MemoryModule("PN", "Kit", 3800, 3800) },
        new MotherboardInfo("MSI", "Z790"),
        new BiosInfo("1.0", null, true),
        chassis);

    private ReposRenduALaCarteTweak Build(uint globalLu = PreferMaxPerfTweak.PreferMax)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetPreferredPstate().Returns(Result<uint>.Ok(globalLu));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new ReposRenduALaCarteTweak(_nvApi, _elevated);
    }

    // Lu au profil du jeu (un jeu tournait), l'état ne dit rien du profil global : ce levier ne sait pas si le bureau est
    // au maximum. La vérification disait « déjà réglé » ; c'est « non lu ». Lu au global, il répond.
    [Fact]
    public void Lu_au_profil_du_jeu_le_global_est_dit_non_lu()
    {
        var t = Build();

        t.EtatNonLu(MaxAuGlobal with { NvidiaDrsScope = @"C:\Jeux\cod.exe" }).Should().BeTrue();
        t.EtatNonLu(MaxAuGlobal with { NvidiaPreferMaxPerf = TriState.Off }).Should().BeFalse("lu au global : déjà au repos");
    }

    [Fact]
    public void Ne_s_applique_qu_au_maximum_lu_au_global_d_un_pc_de_bureau_nvidia()
    {
        var t = Build();
        t.AppliesTo(Machine(), MaxAuGlobal).Should().BeTrue();
        t.AppliesTo(Machine(), MaxAuGlobal with { NvidiaDrsScope = @"C:\Jeux\cod.exe" }).Should()
            .BeFalse("un maximum posé sur le profil du jeu est un choix pour le jeu, pas le bureau");
        t.AppliesTo(Machine(), MaxAuGlobal with { NvidiaPreferMaxPerf = TriState.Off }).Should().BeFalse("déjà au repos");
        t.AppliesTo(Machine(), MaxAuGlobal with { NvidiaPreferMaxPerf = TriState.Unknown }).Should().BeFalse("non lu");
        t.AppliesTo(Machine(ChassisType.Laptop), MaxAuGlobal).Should().BeFalse();
        t.AppliesTo(Machine(vendor: "AMD"), MaxAuGlobal).Should().BeFalse();
    }

    [Fact]
    public async Task Ecrit_normal_au_global_et_garde_l_original_pour_l_annulation()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == PreferMaxPerfTweak.PreferredPstateSettingId
                                                && o.Value == PreferMaxPerfTweak.Normal && o.ProfileName == "Base Profile"),
            Arg.Any<CancellationToken>());
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson!).Value;
        inverse.Should().Be(new ApplyNvApiDrsSettingOp(PreferMaxPerfTweak.PreferredPstateSettingId, PreferMaxPerfTweak.PreferMax, "Base Profile"));
        PrivilegedOpCodec.Deserialize(r.Value.AppliedValueJson!).Value.Should()
            .Be(new ApplyNvApiDrsSettingOp(PreferMaxPerfTweak.PreferredPstateSettingId, PreferMaxPerfTweak.Normal, "Base Profile"),
                "la relecture compare la carte à cette cible");
    }

    [Fact]
    public async Task L_annulation_reecrit_la_valeur_lue_avant()
    {
        var t = Build();
        var r = await t.ApplyAsync();
        _elevated.ClearReceivedCalls();

        (await t.RevertAsync(r.Value!)).IsOk.Should().BeTrue();

        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.Value == PreferMaxPerfTweak.PreferMax && o.ProfileName == "Base Profile"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rien_n_est_ecrit_si_le_global_n_est_plus_au_maximum()
    {
        var t = Build(globalLu: PreferMaxPerfTweak.Normal);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse("le scan est peut-être vieux : ce qui est lu maintenant décide");
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rien_n_est_ecrit_si_le_global_ne_se_lit_pas()
    {
        var t = Build();
        _nvApi.GetPreferredPstate().Returns(Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI"));

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
