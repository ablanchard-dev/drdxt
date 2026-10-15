using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// Le dernier modèle DLSS au profil d'un seul jeu : deux réglages du pilote (NvApiDriverSettings.h), et leur annulation
// selon ce que le profil portait.
public sealed class SurcoucheDlssTests
{
    private const string Jeu = @"C:\Jeux\JeuB\Binaries\Win64\JeuBClient-Win64-Shipping.exe";
    private readonly INvApi _nv = Substitute.For<INvApi>();

    private void Lu(uint id, uint valeur, DrsValueOrigin origine)
        => _nv.GetDrsForApplication(id, Jeu).Returns(Result<(uint, DrsOriginState)>.Ok((valeur, new DrsOriginState(origine, false, ""))));

    [Fact]
    public void Au_defaut_du_pilote_on_pose_les_deux_et_annuler_les_supprime()
    {
        Lu(SurcoucheDlss.Override, 0, DrsValueOrigin.DriverDefault);
        Lu(SurcoucheDlss.Preset, 0, DrsValueOrigin.DriverDefault);

        var p = SurcoucheDlss.Planifier(_nv, Jeu);

        p.IsOk.Should().BeTrue(p.Error?.Message);
        p.Value.Faire.Should().Equal(
            new ApplyNvApiDrsSettingOp(SurcoucheDlss.Override, 1, Jeu),
            new ApplyNvApiDrsSettingOp(SurcoucheDlss.Preset, SurcoucheDlss.DernierModele, Jeu));
        p.Value.Annuler.Should().Equal(
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Override, Jeu, DrsValueOrigin.DriverDefault, 0),
            new DeleteNvApiDrsSettingOp(SurcoucheDlss.Preset, Jeu, DrsValueOrigin.DriverDefault, 0));
    }

    [Fact]
    public void Un_reglage_pose_par_le_joueur_se_rend_a_sa_valeur()
    {
        Lu(SurcoucheDlss.Override, 1, DrsValueOrigin.SetByUser);
        Lu(SurcoucheDlss.Preset, 11, DrsValueOrigin.SetByUser);

        SurcoucheDlss.Planifier(_nv, Jeu).Value.Annuler.Should().Contain(new ApplyNvApiDrsSettingOp(SurcoucheDlss.Preset, 11, Jeu));
    }

    [Fact]
    public void Deja_au_dernier_modele_rien_a_proposer()
    {
        Lu(SurcoucheDlss.Override, 1, DrsValueOrigin.SetByUser);
        Lu(SurcoucheDlss.Preset, SurcoucheDlss.DernierModele, DrsValueOrigin.SetByUser);
        SurcoucheDlss.Planifier(_nv, Jeu).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Sans_profil_lisible_ou_d_origine_inconnue_rien_n_est_propose()
    {
        _nv.GetDrsForApplication(SurcoucheDlss.Override, Jeu).Returns(Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile, "x"));
        SurcoucheDlss.Planifier(_nv, Jeu).IsOk.Should().BeFalse("DrDXT ne crée pas de profil et n'écrit jamais au global à sa place");

        Lu(SurcoucheDlss.Override, 0, DrsValueOrigin.Unknown);
        Lu(SurcoucheDlss.Preset, 0, DrsValueOrigin.DriverDefault);
        SurcoucheDlss.Planifier(_nv, Jeu).IsOk.Should().BeFalse();
    }

    [Theory]
    [InlineData(SurcoucheDlss.Override, 1u, true)]
    [InlineData(SurcoucheDlss.Override, 2u, false)]
    [InlineData(SurcoucheDlss.Preset, 0x00FFFFFFu, true)]
    [InlineData(SurcoucheDlss.Preset, 15u, true)]
    [InlineData(SurcoucheDlss.Preset, 16u, false)]
    public void Les_valeurs_admises_sont_celles_de_nvidia(uint id, uint valeur, bool admise)
        => ValeursDrs.Documentee(id, valeur).Should().Be(admise);
}
