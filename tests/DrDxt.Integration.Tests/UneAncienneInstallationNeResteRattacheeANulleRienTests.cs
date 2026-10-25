using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// La 0.9.1 acceptait /DIR hors Program Files : son dossier, écrivable par le compte, garde un helper et un lanceur. Après la
// mise à jour (UsePreviousAppDir=no), plus rien d'élevé ne doit y pointer : ni la tâche HIGHEST de la 0.9.1 (même quand la
// mise à jour est refusée), ni notre service PresentMon dont l'ancien chemin ne contient pas \DrDXT\, ni le raccourci du
// Bureau. Le dossier n'est pas supprimé en administrateur (écrivable par le compte, une jonction y mènerait ailleurs) : le
// joueur le lit. Le script n'est lu ici que comme du texte : le gate installeur.py le compile.
public sealed class UneAncienneInstallationNeResteRattacheeANulleRienTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss")).Replace("\r\n", "\n");
    }

    private static string Corps(string iss, string entete)
        => Regex.Match(iss, "^" + Regex.Escape(entete) + ".*?^end;", RegexOptions.Multiline | RegexOptions.Singleline).Value;

    [Fact]
    public void La_tache_de_la_0_9_1_part_avant_tout_refus_de_la_mise_a_jour()
    {
        var preparer = Corps(Iss(), "function PrepareToInstall(var NeedsRestart: Boolean): String;");
        var retrait = preparer.IndexOf("'/Delete /F /TN \"DrDXT releve\"'", StringComparison.Ordinal);
        retrait.Should().BeGreaterThan(0, "la tâche HIGHEST de la 0.9.1 pointe vers l'ancien dossier ; elle est recréée après la copie");
        retrait.Should().BeLessThan(preparer.IndexOf("if not DossierAdmis() then", StringComparison.Ordinal), "même un refus ne la laisse pas");
    }

    // sc stop rend la main avant l'arrêt : un service qui tient encore son .exe faisait échouer le déplacement de l'ancien
    // dossier. Chaque arrêt attend l'état STOPPED avant PreparerLeDossier.
    [Fact]
    public void Chaque_service_arrete_attend_son_arret_avant_le_deplacement_du_dossier()
    {
        var iss = Iss();
        var preparer = Corps(iss, "function PrepareToInstall(var NeedsRestart: Boolean): String;");
        var deplacement = preparer.IndexOf("if not PreparerLeDossier() then", StringComparison.Ordinal);
        foreach (var service in new[] { "{#RcService}", "{#PmService}" })
        {
            var arret = preparer.IndexOf($"'stop {service}'", StringComparison.Ordinal);
            var attente = preparer.IndexOf($"AttendreLArret('{service}', 30);", StringComparison.Ordinal);
            arret.Should().BeGreaterThan(0, service);
            attente.Should().BeGreaterThan(arret, service).And.BeLessThan(deplacement, service);
        }
        Corps(iss, "function AttendreLArret(Service: String; Secondes: Integer): Boolean;").Should()
            .Contain("'query ' + Service").And.Contain("'STOPPED'").And.Contain("ResultCode = 1060");
    }

    // À la désinstallation aussi : le service de démarrage arrêté pour de vrai ne réapplique plus rien pendant le rendu, et
    // un service qui tient encore son .exe ne bloque pas le retrait des fichiers.
    [Fact]
    public void La_desinstallation_attend_l_arret_reel_de_chaque_service()
    {
        var desinstaller = Corps(Iss(), "procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");
        foreach (var service in new[] { "{#RcService}", "{#PmService}" })
        {
            var arret = desinstaller.IndexOf($"'stop {service}'", StringComparison.Ordinal);
            arret.Should().BeGreaterThan(0, service);
            desinstaller.IndexOf($"AttendreLArret('{service}', 30);", StringComparison.Ordinal).Should().BeGreaterThan(arret, service)
                .And.BeLessThan(desinstaller.IndexOf($"'delete {service}'", StringComparison.Ordinal), service);
        }
    }

    [Fact]
    public void L_ancien_dossier_se_lit_avant_la_copie_et_notre_service_PresentMon_y_est_reconnu()
    {
        var iss = Iss();
        Corps(iss, "function PrepareToInstall(var NeedsRestart: Boolean): String;").Should().Contain("AncienDossier := DossierPrecedent();");
        Corps(iss, "function DossierPrecedent(): String;").Should()
            .Contain(@"'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7E4D2A1-9C3F-4E56-8A0D-1F2B3C4D5E6F}_is1'")
            .And.Contain("'Inno Setup: App Path'");
        Corps(iss, "function PresentMonEstANous(): Boolean;").Should()
            .Contain("CompareText(Chemin, Uppercase(AddBackslash(AncienDossier) + 'PresentMonService.exe')) = 0",
                "notre ancien service, même hors d'un dossier nommé DrDXT, est repointé sur le dossier verrouillé");
    }

    [Fact]
    public void Le_raccourci_du_Bureau_de_l_ancienne_version_part_et_le_joueur_lit_que_l_ancien_dossier_reste()
    {
        var iss = Iss();
        var installDelete = iss[iss.IndexOf("[InstallDelete]", StringComparison.Ordinal)..];
        installDelete = installDelete[..installDelete.IndexOf("\n[", 1, StringComparison.Ordinal)];
        installDelete.Should().Contain("Type: files; Name: \"{autodesktop}\\DrDXT.lnk\"", "recréé seulement si la case est cochée");

        var etape = Corps(iss, "procedure CurStepChanged(CurStep: TSetupStep);");
        etape.Should().Contain("SignalerLAncienDossier();");
        Corps(iss, "procedure SignalerLAncienDossier();").Should()
            .Contain("CustomMessage('AncienDossierReste')").And.Contain("DirExists(AncienDossier)").And.NotContain("DelTree",
                "jamais supprimé en administrateur : écrivable par le compte");
    }
}
