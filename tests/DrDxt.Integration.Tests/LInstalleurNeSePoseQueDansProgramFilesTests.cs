using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Hors de Program Files (option /DIR de la ligne de commande), le dossier hérite des droits de son parent : sous C:\, tout
// compte authentifié peut modifier les fichiers que les services SYSTEM lancent, et /reset ne fait que recopier ces droits.
// Le script n'est lu ici que comme du texte : le gate installeur.py le compile.
public sealed class LInstalleurNeSePoseQueDansProgramFilesTests
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
    public void Le_seul_dossier_admis_est_Program_Files_DrDXT_lui_meme_sans_jonction()
    {
        var garde = Corps(Iss(), "function DossierAdmis(): Boolean;");

        garde.Should().Contain("CompareText(", "la casse ne compte pas")
            .And.Contain("ExpandFileName(ExpandConstant('{app}'))", "les .. sont résolus avant de comparer")
            .And.Contain("AddBackslash(ExpandConstant('{commonpf}\\DrDXT'))",
                "le dossier exact, séparateur final compris : C:\\Program FilesX\\ ou un sous-dossier ne passent pas");
        garde.Should().Contain("FindFirst(ExpandConstant('{app}')").And.Contain("and $400",
            "une jonction ou un lien (FILE_ATTRIBUTE_REPARSE_POINT) à la place du dossier mènerait ailleurs");
    }

    [Fact]
    public void Un_dossier_non_admis_arrete_l_installation_avant_toute_copie()
    {
        var preparer = Corps(Iss(), "function PrepareToInstall(var NeedsRestart: Boolean): String;");

        var garde = preparer.IndexOf("if not DossierAdmis() then", StringComparison.Ordinal);
        garde.Should().BeGreaterThan(0);
        // Avant le refus, un seul geste : retirer la tâche élevée de la 0.9.1, qui pointe vers l'ancien dossier.
        System.Text.RegularExpressions.Regex.Matches(preparer[..garde], @"\bExec\([^;]*;").Select(m => m.Value).Should()
            .ContainSingle().Which.Should().Contain("'/Delete /F /TN \"DrDXT releve\"'", "rien ne s'arrête ni ne se copie avant");
        preparer[garde..].Should().MatchRegex(@"^if not DossierAdmis\(\) then\s+begin\s+Result := FmtMessage\(CustomMessage\('HorsProgramFiles'\)",
            "un message non vide arrête l'installation, même silencieuse");
    }

    // Le verdict vient des droits relus, pas d'un code de retour : le script lit chaque objet, et ce qu'il refuse est exécuté
    // Le verdict vient des droits relus par le lanceur natif, lancé par l'installeur et vérifié par empreinte ; PreparationDesDroitsTests l'exécute.
    [Fact]
    public void La_protection_passe_par_le_lanceur_natif_verifie_par_empreinte()
    {
        var iss = Iss();
        iss.Should().Contain("function LanceurPret(): String;", "le lanceur est extrait puis vérifié par empreinte")
            .And.Contain("GetSHA256OfFile(Chemin), '{#EmpreinteLanceur}'");
        Corps(iss, "function Droits(Mode, Chemin, Sddl: String): Boolean;")
            .Should().Contain("ExecAndCaptureOutput(Lanceur, 'droits ' + Mode");
        iss.Should().NotContain("ScriptDesDroits", "le script PowerShell n'existe plus")
            .And.NotContain("powershell", "aucun PowerShell n'est lancé par l'installeur");
    }

    [Fact]
    public void Un_dossier_non_protege_ne_garde_aucun_service_d_une_installation_precedente()
    {
        var etape = Corps(Iss(), "procedure CurStepChanged(CurStep: TSetupStep);");
        var echec = etape[..etape.IndexOf("SuppressibleMsgBox(CustomMessage('ProtectionEchouee')", StringComparison.Ordinal)];
        echec = echec[echec.LastIndexOf("else", StringComparison.Ordinal)..];

        echec.Should().Contain("'delete {#RcService}'", "le service de démarrage, en SYSTEM au prochain démarrage, lancerait ces fichiers")
            .And.Contain("if PresentMonEstANous() then").And.Contain("'delete {#PmService}'");
    }
}
