using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

// Un geste du docteur qui écrit est gardé comme un levier : son inverse vit au journal des leviers, sous le profil du
// joueur. Seuls les inverses que ce geste écrit lui-même se rendent ; un inverse forgé dans le journal ne part jamais.
public sealed class LevierDeGesteTests
{
    private const string Jeu = @"C:\Jeux\cod.exe";
    private readonly IElevatedClient _eleve = Substitute.For<IElevatedClient>();

    public LevierDeGesteTests()
        => _eleve.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));

    private static TweakSnapshot Pose(string id, params PrivilegedOp[] inverses)
        => LevierDeGeste.Snapshot(id, inverses, inverses);

    [Fact]
    public async Task Le_paquet_de_pilote_retire_se_remet_par_son_nom()
    {
        var id = LevierDeGeste.IdPilote("oem12.inf");

        (await new LevierDeGeste(id, _eleve).RevertAsync(Pose(id, new RestoreDriverPackageOp("oem12.inf")))).IsOk.Should().BeTrue();

        await _eleve.Received(1).ExecuteAsync(new RestoreDriverPackageOp("oem12.inf"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Les_deux_reglages_DLSS_du_jeu_se_rendent()
    {
        var id = LevierDeGeste.IdDlss(Jeu);
        var inverses = new PrivilegedOp[]
        {
            new ApplyNvApiDrsSettingOp(SurcoucheDlss.Preset, 1, Jeu),
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Override, Jeu, DrsValueOrigin.InheritedFromGlobalProfile, 0),
        };

        (await new LevierDeGeste(id, _eleve).RevertAsync(Pose(id, inverses))).IsOk.Should().BeTrue();

        await _eleve.Received(2).ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // Le journal vit sous le profil du joueur : un inverse qui n'est pas celui du geste (un autre type d'op, un autre paquet,
    // un autre réglage du pilote, un autre jeu) est refusé, et rien ne part au helper.
    public static TheoryData<string, PrivilegedOp> Forges() => new()
    {
        { LevierDeGeste.IdPilote("oem12.inf"), new RestoreDriverPackageOp("oem99.inf") },
        { LevierDeGeste.IdPilote("oem12.inf"), new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SOFTWARE\X", "Y", RegistryValueType.DWord, "1") },
        { LevierDeGeste.IdDlss(Jeu), new ApplyNvApiDrsSettingOp(0x1057EB71, 1, Jeu) },
        { LevierDeGeste.IdDlss(Jeu), new ApplyNvApiDrsSettingOp(SurcoucheDlss.Preset, 1, @"C:\Autre\jeu.exe") },
        { LevierDeGeste.IdDlss(Jeu), new RestoreDriverPackageOp("oem12.inf") },
    };

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task Un_inverse_qui_n_est_pas_celui_du_geste_ne_part_jamais(string id, PrivilegedOp forge)
    {
        var r = await new LevierDeGeste(id, _eleve).RevertAsync(Pose(id, forge));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _eleve.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Seuls_les_identifiants_de_geste_sont_des_gestes()
    {
        LevierDeGeste.EstUnGeste(LevierDeGeste.IdPilote("oem12.inf")).Should().BeTrue();
        LevierDeGeste.EstUnGeste(LevierDeGeste.IdDlss(Jeu)).Should().BeTrue();
        LevierDeGeste.EstUnGeste("gpu.repos_global_normal").Should().BeFalse();
        LevierDeGeste.EstUnGeste("geste.autre|x").Should().BeFalse("seules les deux familles connues sont des gestes gardés");
    }

    // Comme chaque levier, le rendu d'un geste part hors de la portée d'une pose en cours : marqué comme une pose, il serait
    // acquitté avec elle et rejoué comme une origine à rendre.
    [Fact]
    public async Task Le_rendu_d_un_geste_part_hors_de_la_portee_d_une_pose()
    {
        var eleve = Substitute.For<IElevatedClient>();
        PorteeDePose? vue = null;
        eleve.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(c => { vue = PorteeDePose.Courante; return Task.FromResult(Result.Ok()); });
        var id = LevierDeGeste.IdPilote("oem42.inf");
        var levier = new LevierDeGeste(id, eleve);
        var snapshot = LevierDeGeste.Snapshot(id, [new RestoreDriverPackageOp("oem42.inf")], []);

        using (PorteeDePose.Ouvrir())
            (await levier.RevertAsync(snapshot)).IsOk.Should().BeTrue();

        await eleve.Received(1).ExecuteAsync(Arg.Any<RestoreDriverPackageOp>(), Arg.Any<CancellationToken>());
        vue.Should().BeNull();
    }
}
