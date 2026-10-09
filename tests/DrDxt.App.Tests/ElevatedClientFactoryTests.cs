using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// ElevatedClientFactory.Create() wiring (§9.13 + smoke override):
//   * DRDXT_HELPER_SESSION_KEY set (>=32 bytes base64) -> the LEGACY env-var-keyed
//     TypedNamedPipeElevatedClient, so m2smoke/r2smoke keep working.
//   * no env key -> the launch-on-demand + handshake client (UAC deferred to the
//     first op), OR a fail-clean client if the helper exe cannot be located.
//
// These assert the WIRING DECISION (which client is built); nothing here elevates
// or spawns a process. The env var is set/cleared per test (serialized so they do
// not race each other's process-wide env).
// -----------------------------------------------------------------------
[Collection("EnvSerial")]
public sealed class ElevatedClientFactoryTests : IDisposable
{
    private readonly string? _saved;

    public ElevatedClientFactoryTests()
    {
        _saved = Environment.GetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv, _saved);

    [Fact]
    public void Returns_legacy_env_keyed_client_when_session_key_is_provisioned()
    {
        var key = Convert.ToBase64String(new byte[32]);
        Environment.SetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv, key);

        var client = ElevatedClientFactory.Create();

        // SEC2 (08/10/2026) : enveloppé dans ClientSessionUtilisateur (HKCU par l'App, le reste au helper).
        client.Should().BeOfType<ClientSessionUtilisateur>().Which.VersLeHelper.Should().BeOfType<TypedNamedPipeElevatedClient>(
            "a pre-shared key selects the legacy smoke-compatible client");
    }

    [Fact]
    public void Returns_a_non_null_client_when_no_session_key()
    {
        Environment.SetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv, null);

        var client = ElevatedClientFactory.Create();

        // Either launch-on-demand (helper located next to the test host) or the
        // fail-clean client (helper not located). Both are valid; never null,
        // never the legacy env-keyed client when no key is present.
        client.Should().NotBeNull();
        client.Should().NotBeOfType<TypedNamedPipeElevatedClient>();
    }

    [Fact]
    public void A_malformed_session_key_falls_through_to_the_default_path()
    {
        Environment.SetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv, "not-base64!!!");

        var client = ElevatedClientFactory.Create();

        client.Should().NotBeOfType<TypedNamedPipeElevatedClient>(
            "a malformed key is ignored — never used to key the channel");
    }

    [Fact]
    public void A_too_short_session_key_falls_through_to_the_default_path()
    {
        Environment.SetEnvironmentVariable(
            ElevatedClientFactory.SessionKeyEnv, Convert.ToBase64String(new byte[8]));

        var client = ElevatedClientFactory.Create();

        client.Should().NotBeOfType<TypedNamedPipeElevatedClient>(
            "a key shorter than 32 bytes is rejected, not used");
    }

    // 26/09/2026 : la cle d'environnement se pose dans HKCU\Environment, que tout programme du compte ecrit.
    // L'App installee prenait alors le client legacy, qui ne verifie pas le serveur du tube : un squatteur
    // muni de la meme cle recevait les demandes et faisait afficher « applique » (le defaut D.3 du 11/09,
    // rouvert par une variable). Installee sous Program Files, l'App ignore cette cle.
    [Theory]
    [InlineData(@"C:\Program Files\DrDXT\DrDxt.App.exe", false)]
    [InlineData(@"c:\program files\drdxt\DrDxt.App.exe", false)]
    [InlineData(@"C:\Program Files\DrDXT2\DrDxt.App.exe", false)]
    [InlineData(@"C:\Program FilesX\outil\a.exe", true)]
    [InlineData(@"D:\dev\drdxt\tools\m2smoke\bin\m2smoke.exe", true)]
    [InlineData(null, true)]
    public void La_cle_de_session_d_environnement_n_est_permise_qu_hors_installation(string? processus, bool permise)
        => ElevatedClientFactory.CleDEnvironnementPermise(processus, @"C:\Program Files").Should().Be(permise);
}
