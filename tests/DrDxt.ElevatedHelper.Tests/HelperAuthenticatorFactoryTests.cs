using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// FIX 2 — the on-demand launch flow authenticates the connecting App by
// path AND SHA-256 of its image (the hardened WithImageIdentity gate), by DEFAULT.
//
// HelperAuthenticatorFactory binds the authenticator from the provisioned
// --app-path: it computes the expected hash from that path at startup and uses
// the path+hash authenticator. It never fails open: an unresolved path, a missing
// file, or an unhashable image → deny-by-default (every caller denied).
//
// The file-hash + path-resolution are behind seams so the selection and deny cases
// are unit-tested with fakes (no real exe needed).
// -----------------------------------------------------------------------
public sealed class HelperAuthenticatorFactoryTests
{
    private const string AppPath = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private static readonly byte[] AppHash = { 7, 7, 7, 7 };

    // The App image as resolved by the connecting peer (path + content hash).
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        private readonly CallerImage? _image;
        public FakeIdentity(CallerImage? image) => _image = image;
        public CallerImage? Resolve(int pid) => _image;
    }

    [Fact]
    public void Builds_path_plus_hash_authenticator_when_app_path_hashes()
    {
        // The startup hash of --app-path resolves (file readable) → path+hash auth.
        var auth = HelperAuthenticatorFactory.Build(
            AppPath, hashFile: _ => AppHash, imageIdentity: new FakeIdentity(new CallerImage(AppPath, AppHash)));

        // A peer at the same path AND same hash is authorized.
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_a_swapped_exe_at_the_same_path()
    {
        // The startup hash of --app-path is AppHash; a peer at the same path but a
        // DIFFERENT binary (different hash) is rejected (the gap FIX 2 closes).
        var auth = HelperAuthenticatorFactory.Build(
            AppPath, hashFile: _ => AppHash,
            imageIdentity: new FakeIdentity(new CallerImage(AppPath, new byte[] { 9, 9, 9 })));

        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_by_default_when_app_path_is_null_or_empty()
    {
        HelperAuthenticatorFactory.Build(null, hashFile: _ => AppHash)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
        HelperAuthenticatorFactory.Build("   ", hashFile: _ => AppHash)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    [Fact]
    public void Denies_by_default_when_app_path_cannot_be_hashed()
    {
        // The provisioned path's image cannot be hashed (missing/unreadable) → we
        // Cannot bind a path+hash identity → deny-by-default. Never fall back to
        // path-only, never fail open.
        var auth = HelperAuthenticatorFactory.Build(AppPath, hashFile: _ => null);

        auth.Should().BeOfType<DenyByDefaultCallerAuthenticator>();
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_by_default_when_hashing_throws()
    {
        var auth = HelperAuthenticatorFactory.Build(
            AppPath, hashFile: _ => throw new IOException("locked"));

        auth.Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    // 26/09/2026 : le chemin de confiance arrivait en argument. N'importe quel programme du compte pouvait
    // lancer le helper installe avec --app-path=<lui-meme> : l'UAC affichait le nom de DrDXT, et une fois
    // accepte, le helper obeissait a ce programme. Installe sous Program Files, le helper ne croit plus que
    // les executables de SON dossier (reserve aux administrateurs). En developpement, rien ne change.
    private const string ProgramFiles = @"C:\Program Files";
    private const string DossierInstalle = @"C:\Program Files\DrDXT";

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\piege.exe")]
    [InlineData(@"C:\Program Files\DrDXT2\DrDxt.App.exe")]
    [InlineData(@"C:\Program Files\DrDXT\..\Autre\DrDxt.App.exe")]
    [InlineData(@"C:\Program Files\Autre\DrDxt.App.exe")]
    public void Installe_un_chemin_hors_du_dossier_du_helper_n_est_jamais_de_confiance(string demande)
        => HelperAuthenticatorFactory.CheminsDeConfiance([demande], DossierInstalle, ProgramFiles)
            .Should().BeEmpty();

    [Theory]
    [InlineData(@"C:\Program Files\DrDXT\DrDxt.App.exe")]
    [InlineData(@"C:\Program Files\DrDXT\service\DrDxt.ReconcilerHost.exe")]
    [InlineData(@"c:\program files\drdxt\DrDxt.App.exe")]
    public void Installe_les_executables_du_dossier_du_helper_restent_de_confiance(string demande)
        => HelperAuthenticatorFactory.CheminsDeConfiance([demande], DossierInstalle, ProgramFiles)
            .Should().ContainSingle();

    // 26/09/2026 : la cle de session d'environnement (HKCU\Environment, ecrivable par le compte) faisait passer
    // un helper INSTALLE en mode legacy : l'App, qui l'ignore depuis le t473, ne pouvait plus lui parler.
    // Installe, le helper l'ignore aussi. EstInstalle est la regle commune aux deux decisions.
    [Theory]
    [InlineData(@"C:\Program Files\DrDXT", true)]
    [InlineData(@"C:\Program Files\DrDXT\", true)]
    [InlineData(@"C:\Program FilesX\DrDXT", false)]
    [InlineData(@"D:\dev\drdxt\src\DrDxt.ElevatedHelper\bin\Release", false)]
    [InlineData("", false)]
    public void Le_helper_sait_s_il_est_installe_sous_Program_Files(string dossier, bool installe)
        => HelperAuthenticatorFactory.EstInstalle(dossier, ProgramFiles).Should().Be(installe);

    [Fact]
    public void Hors_de_Program_Files_les_outils_de_developpement_gardent_leur_chemin()
        => HelperAuthenticatorFactory.CheminsDeConfiance(
                [@"D:\dev\drdxt\tools\m2smoke\bin\m2smoke.exe", null, "  "],
                @"D:\dev\drdxt\src\DrDxt.ElevatedHelper\bin\Release", ProgramFiles)
            .Should().Equal(@"D:\dev\drdxt\tools\m2smoke\bin\m2smoke.exe");
}
