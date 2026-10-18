using DrDxt.Lanceur;
using FluentAssertions;
using Xunit;

namespace DrDxt.ElevatedHelper.Tests;

// Un processus élevé hérite de HKCU\Environment, écrivable sans droits ; le runtime
// .NET y lit de quoi charger une DLL avant la première ligne du programme (CORECLR_PROFILER_PATH). Le lanceur natif
// reconstruit l'environnement : rien de ce qu'il reçoit ne passe.
public sealed class EnvironnementPropreTests
{
    [Fact]
    public void L_environnement_reconstruit_ne_porte_aucune_variable_lue_par_le_runtime()
    {
        var env = EnvironnementPropre.Construire(@"C:\Windows", @"C:\Users\joueur\AppData\Local\Temp", @"C:\Users\joueur",
            @"C:\Users\joueur\AppData\Roaming", @"C:\Users\joueur\AppData\Local");

        env.Keys.Should().NotContain(k => EnvironnementPropre.EstUneVariableDuRuntime(k));
        env.Keys.Should().BeEquivalentTo(["SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "OS", "TEMP", "TMP",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PSModulePath"]);
        env["PATH"].Split(';').Should().OnlyContain(p => p.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase),
            "un programme élevé ne cherche ses DLL que sous Windows");
        env["SystemDrive"].Should().Be("C:");
    }

    // La console de réparation est un PowerShell élevé et interactif : sans chemin de modules fixé, il chargeait
    // d'abord ceux de Documents, que le compte écrit sans droits.
    [Fact]
    public void Les_modules_PowerShell_ne_viennent_que_de_Windows()
    {
        var env = EnvironnementPropre.Construire(@"C:\Windows", @"C:\Users\joueur\AppData\Local\Temp", @"C:\Users\joueur",
            @"C:\Users\joueur\AppData\Roaming", @"C:\Users\joueur\AppData\Local");

        env["PSModulePath"].Should().Be(@"C:\Windows\System32\WindowsPowerShell\v1.0\Modules");
    }

    // L'audit de santé lit « %ProgramFiles%\... » et « %ProgramData%\... » au registre ; sans ces
    // variables, le scan élevé ignorait en silence des tâches et des services que le scan simple voyait.
    [Fact]
    public void Les_dossiers_du_systeme_que_l_audit_lit_sont_la()
    {
        var env = EnvironnementPropre.Construire(@"C:\Windows", @"C:\Users\joueur\AppData\Local\Temp", @"C:\Users\joueur",
            @"C:\Users\joueur\AppData\Roaming", @"C:\Users\joueur\AppData\Local",
            new EnvironnementPropre.DossiersDuSysteme(@"C:\Program Files", @"C:\Program Files (x86)", @"C:\Program Files\Common Files",
                @"C:\Program Files (x86)\Common Files", @"C:\ProgramData", @"C:\Users\Public", "joueur", 20, "AMD64"));

        env.Keys.Should().NotContain(k => EnvironnementPropre.EstUneVariableDuRuntime(k));
        env["ProgramFiles"].Should().Be(@"C:\Program Files");
        env["ProgramFiles(x86)"].Should().Be(@"C:\Program Files (x86)");
        env["ProgramW6432"].Should().Be(@"C:\Program Files");
        env["ProgramData"].Should().Be(@"C:\ProgramData");
        env["ALLUSERSPROFILE"].Should().Be(@"C:\ProgramData");
        env["PUBLIC"].Should().Be(@"C:\Users\Public");
        env["USERNAME"].Should().Be("joueur");
        env["NUMBER_OF_PROCESSORS"].Should().Be("20");
        env["PROCESSOR_ARCHITECTURE"].Should().Be("AMD64");
        env["PATH"].Split(';').Should().OnlyContain(p => p.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_gcServer")]
    [InlineData("COMPlus_EnableDiagnostics")]
    public void Les_variables_du_runtime_sont_reconnues(string nom) => EnvironnementPropre.EstUneVariableDuRuntime(nom).Should().BeTrue();

    [Fact]
    public void Sans_profil_les_dossiers_du_compte_ne_sont_pas_inventes()
    {
        var env = EnvironnementPropre.Construire(@"D:\Win\", @"D:\Win\Temp", null, null, null);
        env.Keys.Should().NotContain("USERPROFILE").And.NotContain("APPDATA").And.NotContain("LOCALAPPDATA");
        env["SystemRoot"].Should().Be(@"D:\Win");
    }

    [Fact]
    public void Seuls_les_programmes_de_drdxt_sont_lances()
        => EnvironnementPropre.Executables.Should().BeEquivalentTo(["DrDxt.ElevatedHelper.exe", "DrDxt.App.exe", "DrDXT-Test.exe"]);
    // Le désinstalleur passe par le lanceur, qui attend le helper dans ce seul mode : le mot doit être celui du helper.
    [Fact]
    public void Le_lanceur_reconnait_le_rendu_de_la_desinstallation()
        => EnvironnementPropre.RenduDeDesinstallation.Should().Be(ModeDesinstallation.Argument);

    // Le PowerShell du point de restauration (.NET Framework) ne reçoit aucune variable du runtime, même si le helper
    // n'a pas été lancé par le lanceur ; même liste des deux côtés.
    [Theory]
    [InlineData("COR_PROFILER")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("COMPLUS_Version")]
    public void Le_point_de_restauration_ne_transmet_aucune_variable_du_runtime(string nom)
    {
        var environnement = new Dictionary<string, string?> { [nom] = @"C:\piege.dll", ["PATH"] = @"C:\Windows\System32" };

        DrDxt.ElevatedHelper.Execution.Win32RestorePointActions.RetirerLeRuntime(environnement);

        environnement.Should().ContainKey("PATH").And.NotContainKey(nom);
        DrDxt.ElevatedHelper.Execution.Win32RestorePointActions.VariableDuRuntime(nom)
            .Should().Be(EnvironnementPropre.EstUneVariableDuRuntime(nom));
    }
}
