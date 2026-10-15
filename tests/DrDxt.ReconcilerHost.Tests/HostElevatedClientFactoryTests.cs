using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// HostElevatedClientFactory : construit l'IElevatedClient du contexte service.
//
//   * une clé de session provisionnée (>= 32 octets, base64) → le vrai
//     TypedNamedPipeElevatedClient sur le tube protégé par ACL.
//   * pas de clé (la négociation du helper en contexte service n'est pas encore établie) → un
//     client indisponible à échec propre qui refuse chaque op (Result.Fail), pour qu'une tentative
//     de réconciliation journalise « élévation indisponible » et échoue proprement, sans planter.
//   * jamais une élévation réussie inventée : le client indisponible échoue toujours.
// -----------------------------------------------------------------------
public sealed class HostElevatedClientFactoryTests
{
    private static readonly PrivilegedOp AnyOp = new PingOp();

    [Fact]
    public async Task NoSessionKey_ReturnsFailCleanUnavailableClient_ThatRefusesEveryOp()
    {
        var client = HostElevatedClientFactory.Create(keySource: () => null);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse("no session provisioned → fail-clean, never a faked success");
        result.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public async Task EmptyOrTooShortKey_TreatedAsUnavailable()
    {
        var tooShort = Convert.ToBase64String(new byte[8]);
        var client = HostElevatedClientFactory.Create(keySource: () => tooShort);

        var result = await client.ExecuteAsync(AnyOp, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void ValidKey_ReturnsTheRealTypedPipeClient()
    {
        var key = Convert.ToBase64String(new byte[32]); // 32 octets minimum.
        var client = HostElevatedClientFactory.Create(keySource: () => key);

        // Aucun tube connecté ici (pas de helper en test unitaire) ; on vérifie seulement que la
        // fabrique câble le vrai type de client quand une clé est présente ; l'IPC réel est testé en
        // fumée à part (la négociation en contexte service est un bord différé).
        client.Should().BeOfType<TypedNamedPipeElevatedClient>();
    }
}
