using DrDxt.Reconciler.Reseau;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

/// <summary>Le réglage « protéger mon ping » que le service SYSTEM portera. Il vit sous
/// %ProgramData%\DrDxt (écrit par le helper, jamais par le compte) : le service ne lit jamais
/// %LOCALAPPDATA%, que tout programme du compte peut réécrire (un programme du compte pourrait y nommer un autre exe, en pire sous SYSTEM).</summary>
public sealed class PlafondEnvoiDuServiceTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-plafond-service-{Guid.NewGuid():N}");

    public PlafondEnvoiDuServiceTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlafondEnvoiDuService Magasin() => new(_dossier, verifierConfiance: false);

    [Fact]
    public void Ce_qui_est_retenu_se_relit_a_l_identique()
    {
        Magasin().Retenir(new PlafondRetenuDuService("cod.exe", 20_000_000)).IsOk.Should().BeTrue();

        Magasin().Lire().Should().Be(new PlafondRetenuDuService("cod.exe", 20_000_000));
    }

    // Le service ne porte pas un lanceur ou un binaire de Windows, même retenu par une version
    // antérieure du helper qui ne les refusait pas.
    [Theory]
    [InlineData("steam.exe")]
    [InlineData("explorer.exe")]
    public void Un_lanceur_ou_un_binaire_de_Windows_retenu_n_allume_rien(string exe)
    {
        Magasin().Retenir(new PlafondRetenuDuService(exe, 20_000_000));

        Magasin().Lire().Should().BeNull();
    }

    [Fact]
    public void Rien_de_retenu_se_lit_comme_rien()
        => Magasin().Lire().Should().BeNull();

    [Fact]
    public void Oublier_eteint_la_protection()
    {
        Magasin().Retenir(new PlafondRetenuDuService("cod.exe", 20_000_000));

        Magasin().Oublier().IsOk.Should().BeTrue();

        Magasin().Lire().Should().BeNull();
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("""{"Exe":"","PlafondBps":20000000}""")]
    [InlineData("""{"Exe":"cod.exe","PlafondBps":0}""")]
    [InlineData("""{"Exe":"..\\cod.exe","PlafondBps":20000000}""")]
    public void Un_fichier_abime_ou_incoherent_n_allume_jamais_rien(string contenu)
    {
        File.WriteAllText(Path.Combine(_dossier, PlafondEnvoiDuService.NomFichier), contenu);

        Magasin().Lire().Should().BeNull();
    }

    [Fact]
    public void En_production_un_fichier_pose_dans_un_dossier_non_fiable_est_ignore()
    {
        // Le dossier temporaire du compte n'est pas un dossier d'administrateurs : même un contenu
        // parfait n'y est pas cru. C'est la règle du service, pas une option.
        File.WriteAllText(Path.Combine(_dossier, PlafondEnvoiDuService.NomFichier),
            """{"Exe":"cod.exe","PlafondBps":20000000}""");

        new PlafondEnvoiDuService(_dossier, verifierConfiance: true).Lire().Should().BeNull();
    }
}
