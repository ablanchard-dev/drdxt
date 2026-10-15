using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Le lancement à la demande authentifie l'App qui se connecte par son chemin et par le
// SHA-256 de son image (la porte durcie WithImageIdentity), par défaut.
//
// HelperAuthenticatorFactory construit l'authentificateur à partir du --app-path
// provisionné : il calcule l'empreinte attendue depuis ce chemin au démarrage et utilise
// l'authentificateur chemin + empreinte. Jamais ouvert : chemin non résolu, fichier absent
// ou image impossible à hacher → refus par défaut (tout appelant refusé).
//
// Le hachage de fichier et la résolution de chemin sont derrière des coutures, pour tester
// la sélection et les refus avec des faux (sans vrai exécutable).
// -----------------------------------------------------------------------
public sealed class HelperAuthenticatorFactoryTests
{
    private const string AppPath = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private static readonly byte[] AppHash = { 7, 7, 7, 7 };

    // L'image de l'App telle que résolue pour le pair qui se connecte (chemin + empreinte du contenu).
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        private readonly CallerImage? _image;
        public FakeIdentity(CallerImage? image) => _image = image;
        public CallerImage? Resolve(int pid) => _image;
    }

    [Fact]
    public void Builds_path_plus_hash_authenticator_when_app_path_hashes()
    {
        // L'empreinte de démarrage de --app-path se résout (fichier lisible) → authentification chemin + empreinte.
        var auth = HelperAuthenticatorFactory.Build(
            AppPath, hashFile: _ => AppHash, imageIdentity: new FakeIdentity(new CallerImage(AppPath, AppHash)));

        // Un pair au même chemin et à la même empreinte est autorisé.
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_a_swapped_exe_at_the_same_path()
    {
        // L'empreinte de démarrage de --app-path est AppHash ; un pair au même chemin mais d'un
        // binaire différent (autre empreinte) est refusé : c'est la brèche fermée.
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
        // L'image du chemin provisionné ne peut pas être hachée (absente ou illisible) → impossible
        // de lier une identité chemin + empreinte → refus par défaut. Jamais de repli sur le chemin
        // seul, jamais ouvert.
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

    // Le chemin de confiance arrivait en argument. N'importe quel programme du compte pouvait
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

    // La cle de session d'environnement (HKCU\Environment, ecrivable par le compte) faisait passer
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
