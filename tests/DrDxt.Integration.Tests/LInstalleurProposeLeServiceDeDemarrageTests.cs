using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Le service qui reapplique les reglages au demarrage (DrDxtReconciler) existait dans le
// code, teste, et l'installeur ne le livrait pas : ni publie, ni enregistre. « Survit au boot » restait
// une promesse que rien ne pouvait tenir chez un joueur.
//
// Il devient une CASE de l'installeur, DECOCHEE par defaut : un service SYSTEM qui demarre avec Windows
// ne s'installe pas sans qu'on l'ait choisi. Decochee lors d'une mise a jour, elle retire le service
// qu'une installation precedente aurait pose : le choix du moment fait foi.
//
// Ces tests relisent le script (comme ceux du service de mesure) : le service est un processus a part,
// l'installeur et le code n'ont que ces lignes en commun.
// -----------------------------------------------------------------------
public sealed class LInstalleurProposeLeServiceDeDemarrageTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Lire(params string[] morceaux)
        => File.ReadAllText(Path.Combine(Racine().FullName, Path.Combine(morceaux)));

    private static string Iss() => Lire("installer", "DrDXT.iss");

    private static string? Define(string nom)
    {
        var m = Regex.Match(Iss(), $@"^#define\s+{nom}\s+""([^""]*)""", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? LigneDeTache(string nom)
        => Regex.Match(Iss(), $@"^Name:\s*""{nom}"".*$", RegexOptions.Multiline) is { Success: true } m ? m.Value : null;

    [Fact]
    public void Le_service_de_demarrage_est_une_case_cochee_que_le_joueur_peut_decocher()
    {
        var tache = LigneDeTache("reconciler");
        tache.Should().NotBeNull("sans case, le joueur n'a aucun moyen de refuser le service");
        tache.Should().NotContain("unchecked",
            "sans lui, le releve des parties ne lit plus la temperature du processeur : il est coche par defaut")
            .And.NotContain("fixed", "le joueur doit pouvoir le decocher");
    }

    [Fact]
    public void Le_nom_du_service_est_celui_que_le_code_connait()
    {
        // Le nom vient de l'hote lui-meme (l'ancien desinstalleur de l'App, jamais appele, est retire).
        var code = Regex.Match(Lire("src", "DrDxt.ReconcilerHost", "Program.cs"),
            @"\bServiceName\s*=\s*""([^""]*)""").Groups[1].Value;
        code.Should().NotBeEmpty();
        Define("RcService").Should().Be(code,
            "sinon l'installeur pose, arrete et retire un service qui ne porte pas le nom que l'hote declare");
    }

    [Fact]
    public void Le_service_est_cree_seulement_si_la_case_est_cochee_et_retire_sinon()
    {
        var iss = Iss();
        iss.Should().Contain("WizardIsTaskSelected('reconciler')");
        iss.Should().MatchRegex(@"create \{#RcService\} binPath= ""\\""' \+ ExpandConstant\('\{app\}\\service\\DrDxt\.ReconcilerHost\.exe'\)");
        iss.Should().Contain("start= auto");
        iss.Should().Contain("'delete {#RcService}'",
            "decochee a la mise a jour, la case retire le service d'avant ; et la desinstallation le retire");
    }

    // Un service qui plante ne reapplique plus rien jusqu'au prochain demarrage du PC : Windows le relance deux fois, une
    // minute d'ecart, puis le laisse arrete ; le compte repart apres une journee. Un arret voulu ne relance rien.
    [Fact]
    public void Le_service_qui_plante_est_relance_deux_fois_puis_laisse_arrete()
    {
        var iss = Iss();
        iss.Should().Contain("'failure {#RcService} reset= 86400 actions= restart/60000/restart/60000'");
        iss.IndexOf("'failure {#RcService}", StringComparison.Ordinal)
            .Should().BeLessThan(iss.IndexOf("'start {#RcService}'", StringComparison.Ordinal), "la regle est posee avant le premier demarrage");
    }

    [Fact]
    public void La_desinstallation_arrete_puis_retire_le_service()
    {
        var iss = Iss();
        var desinstallation = iss[iss.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal)..];
        desinstallation.Should().Contain("'stop {#RcService}'");
        desinstallation.Should().Contain("'delete {#RcService}'");
        desinstallation.IndexOf("'stop {#RcService}'", StringComparison.Ordinal)
            .Should().BeLessThan(desinstallation.IndexOf("'delete {#RcService}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Le_script_de_construction_publie_l_hote_dans_service()
    {
        var ps = Lire("installer", "build-installer.ps1");
        ps.Should().Contain(@"src\DrDxt.ReconcilerHost\DrDxt.ReconcilerHost.csproj");
        ps.Should().MatchRegex(@"Join-Path \$publishDir 'service'");
        ps.Should().Contain("DrDxt.ReconcilerHost.exe", "la charge utile est verifiee avant compilation");
    }
}
