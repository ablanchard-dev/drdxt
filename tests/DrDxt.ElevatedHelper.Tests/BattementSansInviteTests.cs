using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le battement d'un bail renouvelait par le client de l'App, qui lance le helper à la demande : helper mort, chaque battement
/// ouvrait une invite Windows, et refusée elle revenait toutes les 40 s, en jeu compris. Le battement ne lance jamais le
/// helper (quel que soit le bail : ventilateur, décalage de la carte, plafond d'envoi) ; seul le retour à l'automatique du
/// ventilateur, une fois, peut ouvrir une invite. Refusée : le journal reste, l'écran le dit, et aucune autre invite.
/// </summary>
public sealed class BattementSansInviteTests
{
    // Le helper est mort : tout ce qui passe par le tube échoue vite.
    private sealed class TubeMort : IElevatedClient
    {
        public int Appels;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Appels++;
            return Task.FromResult(Result.Fail(ErrorCodes.HelperUnreachable, "tube absent (test)"));
        }
    }

    [Fact]
    public async Task Helper_mort_et_invite_refusee_un_seul_lancement_apres_plusieurs_battements()
    {
        var lancements = 0;
        var client = new LaunchOnDemandElevatedClient(_ =>
        {
            lancements++;
            return Task.FromResult(Result.Fail(ErrorCodes.ElevationDeclined, "invite refusée (test)"));
        }, new TubeMort());
        var journal = Substitute.For<IRevertEnAttente>();
        var bail = new HeartbeatSender(client, VentilateurExpert.IdBail, 120);
        await using var essai = new EssaiVentilateur(client, journal, new RestoreGpuFanAutoOp(0), bail);
        Result? dit = null;
        essai.ResteFixe += r => dit = r;

        for (var i = 0; i < 5; i++) await bail.PulseAsync(CancellationToken.None);
        await essai.RenduApresPerte!;

        lancements.Should().Be(1, "seul le retour à l'automatique peut lancer le helper, une fois");
        journal.DidNotReceive().Lever();
        dit!.Error!.Value.Message.Should().Contain("invite refusée (test)", "l'échec arrive à l'écran, qui le dit par sa raison lisible");
    }

    // Tous les baux : le décalage de la carte et le plafond d'envoi battent par le même HeartbeatSender.
    [Fact]
    public async Task Aucun_battement_ne_lance_le_helper()
    {
        var lancements = 0;
        var tube = new TubeMort();
        var client = new ClientSessionUtilisateur(new LaunchOnDemandElevatedClient(_ =>
        {
            lancements++;
            return Task.FromResult(Result.Ok());
        }, tube));
        var bail = new HeartbeatSender(client, ReglageCarteExpert.IdBail, 120);

        for (var i = 0; i < 5; i++) await bail.PulseAsync(CancellationToken.None);

        lancements.Should().Be(0);
        tube.Appels.Should().Be(5, "le battement passe par le tube, sans lancement");
    }

    // Les chemins sans geste du joueur (lancement, veille, entre deux parties) passent par le même client que les clics. Dans
    // leur portée, il ne lance jamais le helper, jusque dans un Task.Run ; hors d'elle, un clic le lance toujours.
    [Fact]
    public async Task Sans_geste_du_joueur_le_client_ne_lance_jamais_le_helper()
    {
        var lancements = 0;
        var tube = new TubeMort();
        var client = new LaunchOnDemandElevatedClient(_ => { lancements++; return Task.FromResult(Result.Ok()); }, tube);
        var op = new RestoreGpuFanAutoOp(0);

        Result ecrit;
        Result<byte[]> lu;
        using (SansGesteDuJoueur.Ouvrir())
        {
            ecrit = await Task.Run(() => client.ExecuteAsync(op, CancellationToken.None));
            lu = await client.ReadAsync(op, CancellationToken.None);
        }

        lancements.Should().Be(0, "aucune invite sans geste, ni pour écrire ni pour lire");
        ecrit.Error?.Message.Should().Be(Docteur.T("Elevation.SansGeste"), "le refus dit pourquoi rien n'a été fait");
        lu.IsOk.Should().BeFalse();
        SansGesteDuJoueur.Actif.Should().BeFalse("la portée se referme");

        await client.ExecuteAsync(op, CancellationToken.None);
        lancements.Should().BeGreaterThan(0, "témoin : hors de la portée, le clic lance le helper");
    }
}
