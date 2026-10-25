using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Élevé par l'UAC, l'installeur reçoit l'environnement de HKCU\Environment, que tout programme du compte écrit sans droits,
// et ses enfants en héritent : un PowerShell lancé élevé y lirait de quoi charger une DLL du compte en
// administrateur. Comme DrDxt.Lanceur pour le helper, l'installeur retire tout dès son démarrage et ne repose que le
// minimum, depuis les constantes d'Inno. Le script n'est lu ici que comme du texte : le gate installeur.py le compile.
public sealed class LInstalleurNeTransmetRienDeLEnvironnementDuCompteTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss")).Replace("\r\n", "\n");
    }

    private static string Corps(string iss, string entete)
    {
        var debut = iss.IndexOf(entete, StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, $"« {entete} » existe");
        var corps = iss[debut..];
        return corps[..corps.IndexOf("\nend;", StringComparison.Ordinal)];
    }

    [Fact]
    public void L_environnement_se_reconstruit_avant_tout_outil()
    {
        var iss = Iss();
        Corps(iss, "function InitializeSetup(): Boolean;").Should().Contain("Result := EnvironnementPropre();");
        Corps(iss, "function InitializeUninstall(): Boolean;").Should().Contain("Result := EnvironnementPropre();",
            "la désinstallation lance aussi des outils en administrateur");
        iss.IndexOf("function InitializeSetup(): Boolean;", StringComparison.Ordinal).Should()
            .BeLessThan(iss.IndexOf("function PrepareToInstall(", StringComparison.Ordinal));
    }

    [Fact]
    public void Chaque_variable_recue_part_et_le_minimum_vient_des_constantes()
    {
        var propre = Corps(Iss(), "function EnvironnementPropre(): Boolean;");

        propre.Should().Contain("for I := 0 to GetArrayLength(Noms) - 1 do").And.Contain("if not EffacerVariable(Noms[I], 0) then",
            "aucune variable du compte ne reste, pas même un windir ou un PATH");
        propre.Should().Contain("SetEnvironmentVariableW('PSModulePath', ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\Modules'));",
            "PowerShell 5.1 ne met pas Documents\\WindowsPowerShell\\Modules en tête");
        propre.Should().Contain("Temp := W + '\\SystemTemp';", "jamais le Temp du compte");
        propre.Should().NotContain("GetEnv(", "rien n'est relu de l'environnement reçu");
        propre.Should().Contain("if GetArrayLength(Noms) = 0 then", "un bloc illisible arrête l'installation");
    }
}
