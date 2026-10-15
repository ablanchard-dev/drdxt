using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostElevatedClientFactory.CreateConnectOnly : le client réconciliateur → helper.
//
// Le service réconciliateur peu privilégié ne lance pas le helper (pas d'UAC au démarrage) :
// il se connecte seulement au service helper déjà en route par la même négociation de clé de
// session que l'App, avec l'exécutable de l'hôte réconciliateur comme identité d'appelant. Le
// client côté réconciliateur est donc « connexion seule » : un HandshakingElevatedClient sans
// enveloppe de lancement runas.
//
//   * Il rend le vrai client négociant (connexion seule), jamais l'enveloppe de
//     lancement à la demande.
//   * Quand le service helper est absent, le client échoue proprement (HELPER_UNREACHABLE)
//     plutôt que de planter : le réconciliateur journalise « élévation indisponible » et ne
//     réapplique pas. Vérifié avec un tube inexistant et un délai de connexion court.
// -----------------------------------------------------------------------
public sealed class HostConnectOnlyElevatedClientTests
{
    private static readonly PrivilegedOp AnyOp = new PingOp();

    [Fact]
    public void CreateConnectOnly_returns_the_real_connect_only_handshaking_client()
    {
        var client = HostElevatedClientFactory.CreateConnectOnly();
        // C'est le client négociant en connexion seule, pas l'enveloppe de lancement à la demande
        // (le réconciliateur ne doit jamais déclencher un UAC au démarrage).
        client.Should().BeOfType<HandshakingElevatedClient>();
    }

    [Fact]
    public async Task When_the_helper_is_absent_the_connect_only_client_fails_clean()
    {
        // Un tube unique jamais servi et une échéance de connexion courte → le client ne peut pas
        // atteindre un helper et doit échouer proprement (pas de plantage), en remontant HELPER_UNREACHABLE.
        var deadPipe = "DrDXT.ReconConnectOnly." + Guid.NewGuid().ToString("N");
        var client = HostElevatedClientFactory.CreateConnectOnly(
            pipeName: deadPipe, connectTimeoutMs: 250);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse("no helper service is listening → fail clean, never crash");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable);
    }
}
