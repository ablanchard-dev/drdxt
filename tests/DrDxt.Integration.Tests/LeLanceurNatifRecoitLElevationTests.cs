using FluentAssertions;
using Xunit;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 10/10/2026 (revue de sécurité) : un processus élevé par l'UAC hérite de HKCU\Environment, écrivable par tout
/// programme du compte ; le runtime .NET y lit de quoi charger une DLL avant la première ligne (CORECLR_PROFILER_PATH,
/// documenté par Microsoft). Le seul programme de DrDXT qui reçoit l'élévation est donc natif (NativeAOT, sans CoreCLR),
/// et il redémarre le vrai programme avec un environnement reconstruit. Ce test fige : le lanceur est bien natif, et
/// l'installeur comme le zip de validation le livrent et passent par lui.
/// </summary>
public sealed class LeLanceurNatifRecoitLElevationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RepoRoot(), .. chemin]));

    [Fact]
    public void Le_lanceur_est_compile_en_natif_sans_crochet_de_demarrage()
    {
        var csproj = Lire("src", "DrDxt.Lanceur", "DrDxt.Lanceur.csproj");
        csproj.Should().Contain("<PublishAot>true</PublishAot>", "sans CoreCLR, les variables CORECLR_* n'ont aucune prise");
        csproj.Should().Contain("<StartupHookSupport>false</StartupHookSupport>");
        Lire("src", "DrDxt.Lanceur", "app.manifest").Should().Contain("level=\"asInvoker\"", "un double-clic n'ouvre pas d'invite Windows");
    }

    [Fact]
    public void L_installeur_livre_le_lanceur_et_le_zip_de_validation_passe_par_lui()
    {
        var build = Lire("installer", "build-installer.ps1");
        build.Should().Contain("DrDxt.Lanceur.csproj").And.Contain("'DrDxt.Lanceur.exe'");
        Lire("tools", "amd-harness", "paquet-rapport.ps1").Should().Contain("DrDxt.Lanceur.csproj");
        Lire("tools", "amd-harness", "VALIDER.cmd").Should().Contain("DrDxt.Lanceur.exe\" DrDXT-Test.exe --valider");
        Lire("tools", "amd-harness", "REMETTRE.cmd").Should().Contain("DrDxt.Lanceur.exe\" DrDXT-Test.exe --remettre");
    }

    [Fact]
    public void Le_helper_et_l_app_ne_se_lancent_plus_eleves_sans_passer_par_le_lanceur()
    {
        // Les deux seuls sites qui demandent l'élévation passent par la cible « lanceur s'il est là ».
        Lire("src", "DrDxt.ElevatedHelper", "Ipc", "HelperLauncher.cs").Should().Contain("Cible(spec.HelperExePath)");
        var app = Lire("src", "DrDxt.App", "AppServiceCollectionExtensions.cs");
        app.Should().NotContain("Verb = \"runas\"", "l'App ne se relance plus élevée elle-même");
        app.Should().Contain("RelanceElevee.Demarrer(");
    }
}
