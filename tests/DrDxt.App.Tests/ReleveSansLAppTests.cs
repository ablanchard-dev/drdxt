using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une partie jouée App fermée est relevée. Lancé par un raccourci du dossier Démarrage, le relevé tournerait sans
// droits administrateur et la température du processeur resterait vide (PawnIO refuse). L'installeur pose une tâche
// planifiée au niveau le plus élevé, à l'ouverture de session ; la désinstallation la retire, et retire aussi le
// raccourci d'une ancienne version.
public sealed class ReleveSansLAppTests
{
    private static string Iss()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "installer", "DrDXT.iss"));
    }

    // La tâche HIGHEST hérite de HKCU\Environment, que tout programme du compte écrit sans droits
    // (CORECLR_PROFILER… charge une DLL dans un programme .NET élevé). Elle passe donc par le lanceur natif, qui reconstruit
    // l'environnement ; et elle n'est créée que si la protection du dossier a réussi (sinon l'exe resterait remplaçable).
    [Fact]
    public void L_installeur_pose_le_releve_au_niveau_le_plus_eleve_par_le_lanceur_et_seulement_dossier_protege()
    {
        var iss = Iss();
        var run = iss[iss.IndexOf("[Run]", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("\n[", 1, StringComparison.Ordinal)];
        run.Should().NotContain("schtasks", "aucune tâche élevée créée sans condition");

        var creation = iss.Split('\n').Single(l => l.Contains("schtasks.exe") && l.Contains("/Create"));
        creation.Should().Contain("/SC ONLOGON").And.Contain("/RL HIGHEST").And.Contain("DrDxt.Lanceur.exe")
            .And.Contain("DrDxt.App.exe --releve").And.NotContain("/RU", "sans compte nommé, la tâche est celle du joueur qui installe");
        var protege = iss.IndexOf("if ProtegerLeDossier() then", StringComparison.Ordinal);
        var sinon = iss.IndexOf("else", protege, StringComparison.Ordinal);
        var ou = iss.IndexOf(creation, StringComparison.Ordinal);
        (protege < ou && ou < sinon).Should().BeTrue("créée dans la branche où le dossier est protégé");
        iss.Should().Contain("/Run /TN \"DrDXT releve\"", "la prochaine partie se relève sans attendre une nouvelle session");
    }

    [Fact]
    public void La_desinstallation_retire_la_tache_et_le_raccourci_des_installations_d_avant()
    {
        var iss = Iss();
        iss.Should().Contain("schtasks.exe'), '/Delete /F /TN \"DrDXT releve\"'", "retirée par le [Code] de désinstallation, avec le service");
        iss.Should().Contain("[UninstallDelete]").And.Contain("Type: files; Name: \"{userstartup}\\DrDXT releve.lnk\"");
        iss.Should().Contain("[InstallDelete]");
        iss.IndexOf("Type: files; Name: \"{userstartup}\\DrDXT releve.lnk\"", StringComparison.Ordinal)
            .Should().BeLessThan(iss.IndexOf("[UninstallDelete]", StringComparison.Ordinal), "retiré aussi à la mise à jour");
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
}
