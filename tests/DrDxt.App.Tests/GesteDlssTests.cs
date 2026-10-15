using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Le bouton « dernier modèle DLSS » : les deux réglages partent au profil du jeu, Annuler rend ce que le profil portait.
public sealed class GesteDlssTests
{
    private const string Jeu = @"C:\Jeux\W\Binaries\Win64\W-Win64-Shipping.exe";

    [Fact]
    public async Task Faire_pose_les_deux_reglages_et_annuler_les_rend()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu)
            .Returns(Result<(uint, DrsOriginState)>.Ok((0u, new DrsOriginState(DrsValueOrigin.DriverDefault, false, ""))));
        var envoyes = new List<PrivilegedOp>();

        var g = ScanViewModel.GesteDlss(nv, Jeu, op => { envoyes.Add(op); return Task.FromResult(Result.Ok()); }, c => c)!;
        await g.FaireCommand.ExecuteAsync(null);
        await g.AnnulerCommand.ExecuteAsync(null);

        envoyes.Should().Equal(
            new ApplyNvApiDrsSettingOp(SurcoucheDlss.Override, 1, Jeu),
            new ApplyNvApiDrsSettingOp(SurcoucheDlss.Preset, SurcoucheDlss.DernierModele, Jeu),
            // Annuler défait dans l'ordre inverse de la pose.
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Preset, Jeu, DrsValueOrigin.DriverDefault, 0),
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Override, Jeu, DrsValueOrigin.DriverDefault, 0));
    }

    [Fact]
    public async Task Un_second_reglage_refuse_defait_le_premier()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu)
            .Returns(Result<(uint, DrsOriginState)>.Ok((0u, new DrsOriginState(DrsValueOrigin.DriverDefault, false, ""))));
        var envoyes = new List<PrivilegedOp>();
        Task<Result> Envoyer(PrivilegedOp op)
        {
            envoyes.Add(op);
            return Task.FromResult(op is ApplyNvApiDrsSettingOp { SettingId: SurcoucheDlss.Preset }
                ? Result.Fail(ErrorCodes.HelperOpFailed, "refus") : Result.Ok());
        }

        var g = ScanViewModel.GesteDlss(nv, Jeu, Envoyer, c => c)!;
        await g.FaireCommand.ExecuteAsync(null);

        g.Fait.Should().BeFalse();
        envoyes.Last().Should().Be(new DeleteNvApiDrsSettingOp(SurcoucheDlss.Override, Jeu, DrsValueOrigin.DriverDefault, 0));
    }

    [Fact]
    public void Sans_profil_pilote_aucun_bouton()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu).Returns(Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile, "x"));
        ScanViewModel.GesteDlss(nv, Jeu, _ => Task.FromResult(Result.Ok()), c => c).Should().BeNull();
    }
}
