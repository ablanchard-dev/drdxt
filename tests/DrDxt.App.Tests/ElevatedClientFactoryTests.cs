using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Câblage d'ElevatedClientFactory.Create() (négociation + dérogation des outils de fumée) :
//   * DRDXT_HELPER_SESSION_KEY posée (>= 32 octets en base64) → l'ancien
//     TypedNamedPipeElevatedClient à clé d'environnement, pour que m2smoke/r2smoke marchent toujours.
//   * pas de clé d'environnement → le client à lancement à la demande + négociation (UAC repoussé
//     à la première op), ou un client à échec propre si l'exécutable du helper est introuvable.
//
// Ces tests vérifient la décision de câblage (quel client est construit) ; rien ici n'élève
// ni ne lance un processus. La variable d'environnement est posée et effacée par test (en série
// pour qu'ils ne se disputent pas l'environnement du processus).
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

        // Enveloppé dans ClientSessionUtilisateur (HKCU par l'App, le reste au helper), puis dans ClientDesPoses : une
        // écriture HKCU ne passe jamais par le marquage des poses.
        client.Should().BeOfType<ClientSessionUtilisateur>().Which.VersLeHelper.Should().BeOfType<ClientDesPoses>()
            .Which.VersLeHelper.Should().BeOfType<TypedNamedPipeElevatedClient>(
            "a pre-shared key selects the legacy smoke-compatible client");
    }

    [Fact]
    public void Returns_a_non_null_client_when_no_session_key()
    {
        Environment.SetEnvironmentVariable(ElevatedClientFactory.SessionKeyEnv, null);

        var client = ElevatedClientFactory.Create();

        // Soit le lancement à la demande (helper trouvé à côté de l'hôte de test), soit le client à
        // échec propre (helper introuvable). Les deux sont valides ; jamais null, jamais l'ancien
        // client à clé d'environnement quand aucune clé n'est présente.
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

    // La cle d'environnement se pose dans HKCU\Environment, que tout programme du compte ecrit.
    // Si l'App installee la suivait, elle prendrait le client legacy, qui ne verifie pas le serveur du tube : un
    // squatteur muni de la meme cle recevrait les demandes et ferait afficher « applique » (le defaut D.3,
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
