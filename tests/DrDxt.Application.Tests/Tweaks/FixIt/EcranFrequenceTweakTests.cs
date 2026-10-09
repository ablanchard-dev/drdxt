using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// -----------------------------------------------------------------------
// L-ECRAN : l'écran réglé à sa vraie fréquence.
//
// Ce que ces tests défendent, dans l'ordre d'importance :
//   * rien n'est écrit si Windows refuse le mode à l'essai (CDS_TEST),
//   * une relecture qui ne montre pas la cible ramène l'ancien mode, et n'enregistre rien,
//   * sans confirmation du joueur dans le délai, l'ancien mode revient (l'écran noir ne se confirme pas),
//   * l'original est lu avant, et l'annulation le remet puis le relit,
//   * on ne propose que ce qu'on a lu : un état non lu se dit non lu.
// Aucun de ces tests ne touche le vrai écran : les deux seams sont des substituts.
// -----------------------------------------------------------------------
public sealed class EcranFrequenceTweakTests
{
    private const string Ecran = @"\\.\DISPLAY1";
    private static readonly ModeEcran A60 = new(Ecran, 2560, 1440, 32, 60);
    private static readonly ModeEcran A144 = A60 with { Hz = 144 };

    private readonly IDisplayInfo _ecrans = Substitute.For<IDisplayInfo>();
    private readonly IModeEcran _mode = Substitute.For<IModeEcran>();
    private readonly IConfirmationEcran _confirmation = Substitute.For<IConfirmationEcran>();

    private EcranFrequenceTweak Build(int courantHz = 60, int maxHz = 144, bool confirme = true)
    {
        _ecrans.GetPrimaryGameDisplay().Returns(Result<DisplayInfo>.Ok(
            new DisplayInfo(true, false, courantHz, maxHz, Ecran, Largeur: 2560, Hauteur: 1440)));
        _mode.Courant(Ecran).Returns(A60 with { Hz = courantHz }, A144);
        _mode.Tester(Arg.Any<ModeEcran>()).Returns(0);
        _mode.Ecrire(Arg.Any<ModeEcran>(), Arg.Any<bool>()).Returns(0);
        _confirmation.GarderAsync(Arg.Any<int>(), Arg.Any<TimeSpan>()).Returns(Task.FromResult(confirme));
        return new EcranFrequenceTweak(_ecrans, _mode, _confirmation);
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "610.88", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static SettingsState State(RefreshHeadroom? refresh) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        DisplayRefresh: refresh);

    [Fact]
    public void Identite_sans_droits_et_sans_point_de_restauration()
    {
        var t = Build();
        t.Id.Should().Be("fixit.ecran_frequence");
        t.Category.Should().Be(TweakCategory.FixIt);
        // ChangeDisplaySettingsEx sans CDS_GLOBAL enregistre le mode dans le profil de l'utilisateur.
        t.RequiresElevation.Should().BeFalse();
        t.RequiresRestorePoint.Should().BeFalse();
        ((ITweak)t).TakesEffectAfterReboot.Should().BeFalse();
        t.SummaryKey.Should().Be("tweak.fixit.ecran_frequence.confort");
    }

    [Fact]
    public void Propose_seulement_sous_la_frequence_max_lue()
    {
        var t = Build();
        t.AppliesTo(Hw(), State(new RefreshHeadroom(TriState.On, 60, 144))).Should().BeTrue();
        t.AppliesTo(Hw(), State(new RefreshHeadroom(TriState.Off, 144, 144))).Should().BeFalse();
        t.EtatNonLu(State(new RefreshHeadroom(TriState.Off, 144, 144))).Should().BeFalse();
    }

    [Fact]
    public void Non_lu_ne_propose_rien_et_se_dit_non_lu()
    {
        var t = Build();
        t.AppliesTo(Hw(), State(null)).Should().BeFalse();
        t.EtatNonLu(State(null)).Should().BeTrue();
        t.AppliesTo(Hw(), State(new RefreshHeadroom(TriState.Unknown, 60, 0))).Should().BeFalse();
        t.EtatNonLu(State(new RefreshHeadroom(TriState.Unknown, 60, 0))).Should().BeTrue();
    }

    [Fact]
    public async Task Essaie_ecrit_relit_fait_confirmer_puis_enregistre()
    {
        var r = await Build().ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        Received.InOrder(() =>
        {
            _mode.Courant(Ecran);
            _mode.Tester(A144);
            _mode.Ecrire(A144, false);
            _mode.Courant(Ecran);
            _confirmation.GarderAsync(144, TimeSpan.FromSeconds(15));
            _mode.Ecrire(A144, true);
        });
        // L'original est dans le snapshot : c'est lui que l'annulation remet.
        r.Value!.OriginalValueJson.Should().Contain("\"Hz\":60");
    }

    [Fact]
    public async Task Mode_refuse_a_l_essai_rien_n_est_ecrit()
    {
        var t = Build();
        _mode.Tester(Arg.Any<ModeEcran>()).Returns(-2); // DISP_CHANGE_BADMODE

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        _mode.DidNotReceive().Ecrire(Arg.Any<ModeEcran>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Relecture_qui_ne_confirme_pas_ramene_l_ancien_mode()
    {
        var t = Build();
        _mode.Courant(Ecran).Returns(A60, A60);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        _mode.Received(1).Ecrire(A60, false);
        _mode.DidNotReceive().Ecrire(Arg.Any<ModeEcran>(), true);
        await _confirmation.DidNotReceive().GarderAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Sans_confirmation_dans_le_delai_l_ancien_mode_revient()
    {
        var r = await Build(confirme: false).ApplyAsync();

        r.IsOk.Should().BeFalse();
        _mode.Received(1).Ecrire(A60, false);
        _mode.DidNotReceive().Ecrire(Arg.Any<ModeEcran>(), true);
    }

    [Fact]
    public async Task Deja_au_maximum_rien_n_est_ecrit()
    {
        var r = await Build(courantHz: 144, maxHz: 144).ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
        _mode.DidNotReceive().Tester(Arg.Any<ModeEcran>());
        _mode.DidNotReceive().Ecrire(Arg.Any<ModeEcran>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Annuler_remet_l_original_enregistre_et_le_relit()
    {
        var t = Build();
        var snapshot = (await t.ApplyAsync()).Value!;
        _mode.ClearReceivedCalls();
        _mode.Courant(Ecran).Returns(A60);

        (await t.RevertAsync(snapshot)).IsOk.Should().BeTrue();
        _mode.Received(1).Ecrire(A60, true);
    }

    [Fact]
    public async Task Annuler_dont_la_relecture_ne_montre_pas_l_original_echoue()
    {
        var t = Build();
        var snapshot = (await t.ApplyAsync()).Value!;
        _mode.Courant(Ecran).Returns(A144);

        (await t.RevertAsync(snapshot)).IsOk.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_illisible_n_ecrit_rien()
    {
        var t = Build();
        var r = await t.RevertAsync(new TweakSnapshot("fixit.ecran_frequence", "{pas un mode"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        _mode.DidNotReceive().Ecrire(Arg.Any<ModeEcran>(), Arg.Any<bool>());
    }
}
