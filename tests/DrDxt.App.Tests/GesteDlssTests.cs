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

    // Le premier réglage posé ne se défait pas : le geste est resté à moitié posé. « Rien n'a été changé » serait faux ;
    // Annuler reste possible.
    [Fact]
    public async Task Un_premier_reglage_qui_ne_se_defait_pas_laisse_le_geste_a_moitie_et_le_dit()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu)
            .Returns(Result<(uint, DrsOriginState)>.Ok((0u, new DrsOriginState(DrsValueOrigin.DriverDefault, false, ""))));
        Task<Result> Envoyer(PrivilegedOp op) => Task.FromResult(op is ApplyNvApiDrsSettingOp { SettingId: SurcoucheDlss.Preset }
            or DeleteNvApiDrsSettingOp ? Result.Fail(ErrorCodes.HelperOpFailed, "refus") : Result.Ok());

        var g = ScanViewModel.GesteDlss(nv, Jeu, Envoyer, c => c)!;
        await g.FaireCommand.ExecuteAsync(null);

        g.AEchoue.Should().BeFalse();
        g.AMoitie.Should().BeTrue();
        g.PeutAnnuler.Should().BeTrue("un réglage reste posé");
    }

    // Annuler tente chaque réglage : un refus sur le premier n'empêche pas de rendre le second, et le geste se dit à moitié.
    [Fact]
    public async Task Annuler_tente_chaque_reglage_et_un_refus_partiel_se_dit_a_moitie()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu)
            .Returns(Result<(uint, DrsOriginState)>.Ok((0u, new DrsOriginState(DrsValueOrigin.DriverDefault, false, ""))));
        var envoyes = new List<PrivilegedOp>();
        Task<Result> Envoyer(PrivilegedOp op)
        {
            envoyes.Add(op);
            return Task.FromResult(op is DeleteNvApiDrsSettingOp { SettingId: SurcoucheDlss.Preset }
                ? Result.Fail(ErrorCodes.HelperOpFailed, "refus") : Result.Ok());
        }

        var g = ScanViewModel.GesteDlss(nv, Jeu, Envoyer, c => c)!;
        await g.FaireCommand.ExecuteAsync(null);
        await g.AnnulerCommand.ExecuteAsync(null);

        envoyes.OfType<DeleteNvApiDrsSettingOp>().Should().HaveCount(2, "le second inverse part malgré le refus du premier");
        g.AMoitie.Should().BeTrue();
        g.PeutAnnuler.Should().BeTrue();
    }

    [Fact]
    public void Sans_profil_pilote_aucun_bouton()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu).Returns(Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile, "x"));
        ScanViewModel.GesteDlss(nv, Jeu, _ => Task.FromResult(Result.Ok()), c => c).Should().BeNull();
    }

    // Posé, le geste est gardé comme un levier : ses inverses, dans l'ordre où ils se rejouent, sont ceux que le levier de
    // geste admet. Rendu en entier, il s'oublie (journal des leviers et manifeste).
    [Fact]
    public async Task Le_geste_pose_est_garde_et_rendu_s_oublie()
    {
        var nv = Substitute.For<INvApi>();
        nv.GetDrsForApplication(Arg.Any<uint>(), Jeu)
            .Returns(Result<(uint, DrsOriginState)>.Ok((0u, new DrsOriginState(DrsValueOrigin.DriverDefault, false, ""))));
        IReadOnlyList<PrivilegedOp>? gardes = null;
        var oublie = false;

        var g = ScanViewModel.GesteDlss(nv, Jeu, _ => Task.FromResult(Result.Ok()), c => c,
            (inverses, _) => { gardes = inverses; return Task.CompletedTask; }, () => { oublie = true; return Task.CompletedTask; })!;
        await g.FaireCommand.ExecuteAsync(null);

        gardes.Should().Equal(
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Preset, Jeu, DrsValueOrigin.DriverDefault, 0),
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Override, Jeu, DrsValueOrigin.DriverDefault, 0));
        var id = DrDxt.Application.Tweaks.LevierDeGeste.IdDlss(Jeu);
        DrDxt.Application.Tweaks.LevierDeGeste.Inverses(id, CompositeSnapshotCodec.Encode(gardes!).Value).Should().NotBeNull();

        await g.AnnulerCommand.ExecuteAsync(null);
        oublie.Should().BeTrue();
    }
}
