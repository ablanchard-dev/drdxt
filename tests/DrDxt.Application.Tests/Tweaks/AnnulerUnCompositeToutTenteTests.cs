using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// L'annulation d'un levier à plusieurs écritures tente chaque inverse, même après un refus : s'arrêter au premier laisserait
/// les feuilles suivantes posées. Le premier échec est rendu. La pose fait déjà de même (RetourArriere.EchecAsync).
/// </summary>
public sealed class AnnulerUnCompositeToutTenteTests
{
    private static readonly PrivilegedOp Premiere = new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode");
    private static readonly PrivilegedOp Seconde = new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");

    public static TheoryData<string> Composites() => new()
    {
        "latency.vrr_vsync_cap", "fixit.x3d_ccd", "latency.gameModeOn", "latency.mouse_acceleration_off", "fixit.heures_actives",
    };

    private static ITweak Levier(string id, IElevatedClient eleve) => id switch
    {
        "latency.vrr_vsync_cap" => new VrrVsyncCapConfigTweak(Substitute.For<INvApi>(), Substitute.For<IDisplayInfo>(), eleve),
        "fixit.x3d_ccd" => new X3dCcdParkingFixTweak(Substitute.For<IServiceStateReader>(), Substitute.For<IRegistryReader>(), eleve),
        "latency.gameModeOn" => new GameModeOnTweak(Substitute.For<IRegistryReader>(), eleve),
        "latency.mouse_acceleration_off" => new AccelerationSourisOffTweak(Substitute.For<IRegistryReader>(), eleve),
        _ => new HeuresActivesTweak(Substitute.For<IRegistryReader>(), eleve),
    };

    [Theory]
    [MemberData(nameof(Composites))]
    public async Task Un_inverse_refuse_n_arrete_pas_les_suivants(string id)
    {
        var recues = new List<PrivilegedOp>();
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            recues.Add(c.Arg<PrivilegedOp>());
            return c.Arg<PrivilegedOp>() == Premiere ? Result.Fail(ErrorCodes.OpNotAllowed, "refus") : Result.Ok();
        });
        var snapshot = new TweakSnapshot(id, CompositeSnapshotCodec.Encode([Premiere, Seconde]).Value!);

        var r = await Levier(id, eleve).RevertAsync(snapshot);

        recues.Should().Equal(Premiere, Seconde);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.OpNotAllowed, "le premier échec est rendu");
    }

    [Theory]
    [MemberData(nameof(Composites))]
    public async Task Un_inverse_qui_leve_n_arrete_pas_les_suivants(string id)
    {
        var recues = new List<PrivilegedOp>();
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            recues.Add(c.Arg<PrivilegedOp>());
            return c.Arg<PrivilegedOp>() == Premiere ? throw new IOException("tube fermé") : Task.FromResult(Result.Ok());
        });
        var snapshot = new TweakSnapshot(id, CompositeSnapshotCodec.Encode([Premiere, Seconde]).Value!);

        var r = await Levier(id, eleve).RevertAsync(snapshot);

        recues.Should().Equal(Premiere, Seconde);
        r.IsOk.Should().BeFalse();
    }

    private static readonly PrivilegedOp Troisieme = new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled");

    // « Non » à l'invite de Windows, ou helper injoignable : chaque inverse suivant aurait rouvert une invite. Ils partent sans
    // geste du joueur (aucune invite) ; ceux que l'App écrit elle-même passent encore, et tous sont tentés.
    [Theory]
    [MemberData(nameof(Composites))]
    public async Task Apres_un_non_a_l_invite_les_inverses_suivants_partent_sans_invite(string id)
    {
        var sansGeste = new List<bool>();
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            sansGeste.Add(SansGesteDuJoueur.Actif);
            return SansGesteDuJoueur.Actif ? Result.Ok() : Result.Fail(ErrorCodes.ElevationDeclined, "non");
        });
        var snapshot = new TweakSnapshot(id, CompositeSnapshotCodec.Encode([Premiere, Seconde, Troisieme]).Value!);

        var r = await Levier(id, eleve).RevertAsync(snapshot);

        sansGeste.Should().Equal(false, true, true);
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDeclined, "le premier échec est rendu");
        SansGesteDuJoueur.Actif.Should().BeFalse("la portée ne survit pas à l'annulation");
    }

    [Fact]
    public async Task Un_helper_injoignable_fait_partir_les_suivants_sans_invite()
    {
        var sansGeste = new List<bool>();
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            sansGeste.Add(SansGesteDuJoueur.Actif);
            return sansGeste.Count == 1 ? Result.Fail(ErrorCodes.HelperUnreachable, "parti") : Result.Ok();
        });

        await Levier("latency.gameModeOn", eleve).RevertAsync(
            new TweakSnapshot("latency.gameModeOn", CompositeSnapshotCodec.Encode([Premiere, Seconde, Troisieme]).Value!));

        sansGeste.Should().Equal(false, true, true);
    }

    // Un autre refus (la liste blanche) n'a pas d'invite derrière : les suivants partent comme avant.
    [Fact]
    public async Task Un_refus_sans_invite_ne_change_rien_aux_suivants()
    {
        var sansGeste = new List<bool>();
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            sansGeste.Add(SansGesteDuJoueur.Actif);
            return sansGeste.Count == 1 ? Result.Fail(ErrorCodes.OpNotAllowed, "refus") : Result.Ok();
        });

        await Levier("latency.gameModeOn", eleve).RevertAsync(
            new TweakSnapshot("latency.gameModeOn", CompositeSnapshotCodec.Encode([Premiere, Seconde, Troisieme]).Value!));

        sansGeste.Should().Equal(false, false, false);
    }
}
