using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une partie jouée App fermée est relevée, par une tâche planifiée posée à l'ouverture de session ; la désinstallation
// la retire, et retire aussi le raccourci d'une ancienne version. Sans droits : élevé, le relevé écrivait dans le dossier du
// compte, où une jonction posée sans droits devenait une écriture en administrateur.
public sealed class ReleveSansLAppTests
{
    private static string Iss() => Fichier("installer", "DrDXT.iss");

    private static string Fichier(params string[] chemin)
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        // Le dépôt garde LF et chaque poste sort ses fins de ligne : le test lit la même chose partout.
        return System.IO.File.ReadAllText(System.IO.Path.Combine([d!.FullName, .. chemin])).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // Sans droits, jamais HIGHEST, l'App directement : le lanceur, non élevé, demanderait l'élévation à chaque ouverture de
    // session. Créée seulement si la protection du dossier a réussi (l'exe ne doit pas être remplaçable). Le principal est
    // le groupe Utilisateurs : un compte standard installe avec le mot de passe d'un administrateur, et une tâche sans
    // principal serait celle de l'administrateur, jamais celle du joueur.
    [Fact]
    public void L_installeur_pose_le_releve_pour_qui_ouvre_sa_session_sans_droits_et_seulement_dossier_protege()
    {
        var iss = Iss();
        var run = iss[iss.IndexOf("[Run]", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("\n[", 1, StringComparison.Ordinal)];
        run.Should().NotContain("schtasks", "aucune tâche élevée créée sans condition");

        var poser = iss[iss.IndexOf("function PoserLeReleve(): Boolean;", StringComparison.Ordinal)..];
        poser = poser[..poser.IndexOf("\nend;", StringComparison.Ordinal)];
        poser.Should().Contain("<LogonTrigger>").And.Contain("<GroupId>S-1-5-32-545</GroupId>")
            .And.Contain("<RunLevel>LeastPrivilege</RunLevel>").And.NotContain("HighestAvailable").And.NotContain("<UserId>")
            .And.Contain("XmlEchappe(ExpandConstant('{app}') + '\\DrDxt.App.exe')").And.Contain("<Arguments>--releve</Arguments>")
            .And.NotContain("DrDxt.Lanceur.exe", "non élevé, le lanceur demanderait l'élévation");
        poser.Should().Contain("Chemin := DossierDuLanceur() + '\\releve.xml'", "un XML lu en administrateur ne vit pas sous le compte");
        poser.IndexOf("DeleteFile(Chemin)", StringComparison.Ordinal).Should()
            .BeGreaterThan(poser.IndexOf("/XML", StringComparison.Ordinal), "le XML part après la création");
        poser.Should().Contain("and (ResultCode = 0)", "un refus de Windows se voit");

        var repli = iss.Split('\n').Single(l => l.Contains("schtasks.exe") && l.Contains("/SC ONLOGON"));
        poser.Should().Contain("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>")
            .And.Contain("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>")
            .And.Contain("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "le relevé tient toute la session, portable débranché compris");
        iss.Should().Contain("ReleveDeTous := PoserLeReleve();")
            .And.Contain("if not ReleveDeTous then\n          SuppressibleMsgBox(CustomMessage('ReleveCeCompte')",
                "le repli redonne la tâche du compte qui installe : il se dit");
        repli.Should().Contain("ReleveDeTous or").And.Contain("/RL LIMITED").And.NotContain("HIGHEST")
            .And.Contain("\\DrDxt.App.exe\\\" --releve").And.Contain("(ResultCode = 0)");
        var protege = iss.IndexOf("if ProtegerLeDossier() then", StringComparison.Ordinal);
        var sinon = iss.IndexOf("else", protege, StringComparison.Ordinal);
        var ou = iss.IndexOf(repli, StringComparison.Ordinal);
        (protege < ou && ou < sinon).Should().BeTrue("créée dans la branche où le dossier est protégé");
        iss.Should().NotContain("/Run /TN", "un lancement à la demande d'une tâche par groupe n'est pas prouvé")
            .And.Contain("ExecAsOriginalUser(ExpandConstant('{app}\\DrDxt.App.exe'), '--releve'",
                "la prochaine partie se relève sans attendre une nouvelle session")
            .And.Contain("SuppressibleMsgBox(CustomMessage('ReleveNonPose')", "une tâche refusée se dit");
    }

    // Ce que les documents disent de la tâche : un joueur qui lit « au niveau le plus élevé » croit que la température du
    // processeur se relève DrDXT fermé, ce qu'elle ne fait plus sans le service.
    [Theory]
    [InlineData("README.fr.md")]
    [InlineData("README.md")]
    [InlineData("docs/NON-NUISANCE.md")]
    public void Les_documents_ne_disent_pas_le_releve_eleve(string document)
    {
        var paragraphes = System.Text.RegularExpressions.Regex.Split(Fichier(document.Split('/')), @"\r?\n(?=- |\*\*|\r?\n)")
            .Where(p => p.Contains("DrDXT releve", StringComparison.Ordinal)).ToList();
        paragraphes.Should().NotBeEmpty("témoin : le document parle de la tâche");
        paragraphes.Should().NotContain(p => System.Text.RegularExpressions.Regex.IsMatch(p, "(?i)highest|plus élevé"),
            "la tâche est créée en /RL LIMITED");
    }

    [Fact]
    public void La_desinstallation_retire_la_tache_et_le_raccourci_des_installations_d_avant()
    {
        var iss = Iss();
        iss.Should().Contain("schtasks.exe'), '/Delete /F /TN \"DrDXT releve\"'", "retirée par le [Code] de désinstallation, avec le service");
        iss.Should().Contain("\n[UninstallDelete]").And.Contain("Type: files; Name: \"{userstartup}\\DrDXT releve.lnk\"");
        iss.Should().Contain("[InstallDelete]");
        iss.IndexOf("Type: files; Name: \"{userstartup}\\DrDXT releve.lnk\"", StringComparison.Ordinal)
            // La section se cherche en début de ligne : un commentaire de l'iss peut la nommer.
            .Should().BeLessThan(iss.IndexOf("\n[UninstallDelete]", StringComparison.Ordinal), "retiré aussi à la mise à jour");
    }

    [Theory]
    [InlineData("fr", "fermé")]
    [InlineData("en", "closed")]
    public void L_ecran_dit_que_le_releve_tourne_aussi_DrDXT_ferme(string culture, string mot)
    {
        var texte = new ResxLocalizer().Get("Parties.Description", new CultureInfo(culture));
        texte.Should().Contain(mot);
        texte.Should().NotContain("rester ouvert").And.NotContain("must stay open");
        texte.Should().NotContain("20 s").And.NotContain("trois minutes").And.NotContain("three minutes", "11/10 : toute la partie est mesurée");
    }

    // Lancé sans fenêtre, le relevé ne lance jamais le helper (aucune invite) et ne pose ni ne rend aucun levier : l'essai en
    // alternance n'avance que dans l'App.
    [Fact]
    public void Le_releve_de_fond_ne_lance_pas_le_helper_et_ne_pose_aucun_levier()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml.cs"));

        app.Should().MatchRegex(@"if \(releveDeFond\)\s*collection\.AddSingleton<[\w.]*IElevatedClient>\([^;]*CreateSansLancer\(\)\);",
            "le client sans lancement, quel que soit le niveau du processus");
        var debut = app.IndexOf("private void ReleverEnFond(", StringComparison.Ordinal);
        debut.Should().BePositive();
        var corps = app[debut..app.IndexOf("\n    }", debut, StringComparison.Ordinal)];
        corps.Should().NotContain("LevierDeLEssai =").And.NotContain("HelperJoignableSansUac =");
    }
}
