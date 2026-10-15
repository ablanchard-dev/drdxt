using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// HelperAuthenticatorFactory.BuildMulti : construit l'authentificateur du helper à partir
// d'un ensemble d'images appelantes de confiance (l'App interactive et l'hôte
// réconciliateur), chacune par chemin + SHA-256 (le durcissement appliqué image par image).
//
// Règles de sélection et de fermeture (miroir du Build à une image, par membre) :
//   * chaque chemin provisionné dont l'image se hache au démarrage → un membre chemin + empreinte ;
//   * le composite autorise un pair s'il correspond à un de ces membres ;
//   * un pair qui n'en est aucun (tiers, ou binaire substitué à un chemin de confiance)
//     → refus ;
//   * si aucun chemin ne se résout ni ne se hache (tous nuls, vides ou illisibles) → refus
//     par défaut (jamais ouvert, jamais chemin seul, jamais tout accepter).
//
// Le hachage de fichier et la résolution de l'image du pair sont derrière des coutures (sans vrai exécutable).
// -----------------------------------------------------------------------
public sealed class HelperMultiCallerAuthenticatorFactoryTests
{
    private const string AppPath = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private const string ReconPath = @"C:\Program Files\DrDXT\DrDxt.ReconcilerHost.exe";
    private const string ThirdPartyPath = @"C:\Temp\evil.exe";

    private static readonly byte[] AppHash = { 1, 1, 1, 1 };
    private static readonly byte[] ReconHash = { 2, 2, 2, 2 };

    // Résout le couple (chemin, empreinte) du pair qui se connecte par pid. Chaque pid de
    // confiance renvoie sa vraie image ; un pid inconnu renvoie une image tierce.
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        public CallerImage? Resolve(int pid) => pid switch
        {
            1 => new CallerImage(AppPath, AppHash),
            2 => new CallerImage(ReconPath, ReconHash),
            3 => new CallerImage(ThirdPartyPath, new byte[] { 9, 9, 9 }),
            // un binaire substitué au chemin de confiance de l'App (bon chemin, mauvaise empreinte) :
            4 => new CallerImage(AppPath, new byte[] { 8, 8, 8 }),
            _ => null,
        };
    }

    // Le hacheur de démarrage : hache chaque chemin de confiance vers son empreinte attendue.
    private static byte[]? HashFile(string path) => path switch
    {
        AppPath => AppHash,
        ReconPath => ReconHash,
        _ => null,
    };

    private static ICallerAuthenticator BuildBoth() =>
        HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath }, hashFile: HashFile, imageIdentity: new FakeIdentity());

    [Fact]
    public void App_caller_is_authorized()
    {
        BuildBoth().Authorize(new CallerInfo(1)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Reconciler_caller_is_authorized()
    {
        BuildBoth().Authorize(new CallerInfo(2)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Third_party_caller_is_denied()
    {
        var r = BuildBoth().Authorize(new CallerInfo(3));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Swapped_binary_at_a_trusted_path_is_denied()
    {
        BuildBoth().Authorize(new CallerInfo(4)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Unhashable_paths_are_skipped_but_resolvable_ones_still_authorize()
    {
        // ReconPath ne se hache pas au démarrage → il est sauté, mais l'App marche toujours.
        var auth = HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath },
            hashFile: p => p == AppPath ? AppHash : null,
            imageIdentity: new FakeIdentity());

        auth.Authorize(new CallerInfo(1)).IsOk.Should().BeTrue("App still hashes");
        auth.Authorize(new CallerInfo(2)).IsOk.Should().BeFalse("Recon was skipped (unhashable)");
    }

    [Fact]
    public void Denies_by_default_when_no_path_resolves()
    {
        HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath }, hashFile: _ => null, imageIdentity: new FakeIdentity())
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    [Fact]
    public void Denies_by_default_when_the_set_is_empty_or_all_blank()
    {
        HelperAuthenticatorFactory.BuildMulti(Array.Empty<string>(), hashFile: HashFile)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
        HelperAuthenticatorFactory.BuildMulti(new[] { "  ", "" }, hashFile: HashFile)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    [Fact]
    public void A_single_trusted_path_still_works_through_BuildMulti()
    {
        var auth = HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath }, hashFile: HashFile, imageIdentity: new FakeIdentity());
        auth.Authorize(new CallerInfo(1)).IsOk.Should().BeTrue();
        auth.Authorize(new CallerInfo(2)).IsOk.Should().BeFalse();
    }
}
