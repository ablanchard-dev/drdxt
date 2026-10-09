using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.Reconciler.Reseau;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>28/09/2026 : retenir la protection du ping pour le service de démarrage. Le helper élevé est le
/// seul à écrire ce réglage sous %ProgramData%\DrDxt, derrière la même borne que le plafond lui-même.</summary>
public sealed class PlafondRetenuPourLeServiceTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-retenu-{Guid.NewGuid():N}");

    public PlafondRetenuPourLeServiceTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PrivilegedOpExecutor Executeur(PlafondEnvoiDuService? magasin) => new(
        Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
        Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
        Substitute.For<IADLXTuningActions>(), plafondDuService: magasin);

    [Fact]
    public void L_allowlist_applique_la_borne_du_plafond_lui_meme()
    {
        var liste = new OpAllowlist();

        liste.Validate(new RetainServiceUploadCapOp("cod.exe", 20_000_000)).IsOk.Should().BeTrue();
        liste.Validate(new RetainServiceUploadCapOp(@"..\cod.exe", 20_000_000)).IsOk.Should().BeFalse();
        liste.Validate(new RetainServiceUploadCapOp("cod.exe", 1)).IsOk.Should().BeFalse();
        liste.Validate(new ForgetServiceUploadCapOp()).IsOk.Should().BeTrue();
    }

    // 28/09/2026 : retenu pour le service, un lanceur ou un binaire de Windows serait exempté à chaque session,
    // redémarrages compris, pendant que tout le reste est bridé. Même liste que le rallumage de l'App.
    [Theory]
    [InlineData("steam.exe")]
    [InlineData("Discord.exe")]
    [InlineData("explorer.exe")]
    public void Un_lanceur_ou_un_binaire_de_Windows_n_est_pas_retenu_pour_le_service(string exe)
        => new OpAllowlist().Validate(new RetainServiceUploadCapOp(exe, 20_000_000)).IsOk.Should().BeFalse();

    // La pose directe passait avec un simple nom valide : un appelant qui contournerait les filtres de l'App
    // pouvait brider toute la machine sauf explorer.exe. Le helper tient la même règle que pour le service.
    [Theory]
    [InlineData("steam.exe")]
    [InlineData("chrome.exe")]
    [InlineData("explorer.exe")]
    public void Un_lanceur_ou_un_binaire_de_Windows_n_est_pas_exempte_non_plus_en_pose_directe(string exe)
        => new OpAllowlist().Validate(new ApplyUploadCapOp(exe, 20_000_000)).IsOk.Should().BeFalse();

    [Fact]
    public async Task Retenir_puis_oublier_passe_par_le_magasin_du_service()
    {
        var magasin = new PlafondEnvoiDuService(_dossier, verifierConfiance: false);
        var executeur = Executeur(magasin);

        (await executeur.ExecuteAsync(new RetainServiceUploadCapOp("cod.exe", 20_000_000), CancellationToken.None)).IsOk.Should().BeTrue();
        magasin.Lire().Should().Be(new PlafondRetenuDuService("cod.exe", 20_000_000));

        (await executeur.ExecuteAsync(new ForgetServiceUploadCapOp(), CancellationToken.None)).IsOk.Should().BeTrue();
        magasin.Lire().Should().BeNull();
    }

    [Fact]
    public async Task Sans_magasin_branche_l_ordre_echoue_honnetement()
    {
        var r = await Executeur(null).ExecuteAsync(new RetainServiceUploadCapOp("cod.exe", 20_000_000), CancellationToken.None);

        r.IsOk.Should().BeFalse("le service n'a pas à écrire son propre réglage : seul le helper le branche");
    }
}
