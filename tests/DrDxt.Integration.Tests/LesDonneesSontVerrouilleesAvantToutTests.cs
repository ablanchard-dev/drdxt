using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// ProgramData\DrDxt porte ce que SYSTEM croit (clé et manifeste signé, pause du maintien, témoins, journaux du helper).
// ProgramData laisse les comptes y créer des dossiers : avant la première exécution élevée, un programme du compte pouvait
// le créer d'avance, y poser une jonction (« temoins » vers System32) ou un fichier piégé. L'installeur le prépare avant
// tout, par le lanceur natif : une racine non fiable part de côté et la neuve naît durcie ; un lien ou un dossier du
// compte part de côté ; un fichier du compte reste à lui, hors confiance, et ses lecteurs l'ignorent. Rien n'y est jamais
// recalculé ni pris aux Administrateurs. La désinstallation ne vide plus « temoins » à l'aveugle. Le script n'est lu ici que
// comme du texte : le gate installeur.py le compile, PreparationDesDroitsTests exécute le module natif.
public sealed class LesDonneesSontVerrouilleesAvantToutTests
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
    public void Les_donnees_se_verrouillent_avant_la_copie_et_un_echec_arrete_l_installation()
    {
        var preparer = Corps(Iss(), "function PrepareToInstall(var NeedsRestart: Boolean): String;");
        var donnees = preparer.IndexOf("if not VerrouillerLesDonnees() then", StringComparison.Ordinal);
        donnees.Should().BeGreaterThan(preparer.IndexOf("if not PreparerLeDossier() then", StringComparison.Ordinal));
        preparer[donnees..].Should().MatchRegex(@"^if not VerrouillerLesDonnees\(\) then\s+begin\s+Result := CustomMessage\('DonneesImpossibles'\)");
    }

    [Fact]
    public void La_racine_se_prepare_par_le_script_avec_la_protection_du_code()
    {
        var iss = Iss();
        Corps(iss, "function VerrouillerLesDonnees(): Boolean;")
            .Should().Contain("Result := Droits('donnees', ExpandConstant('{commonappdata}\\DrDxt'), SddlDonnees);");
        iss.Should().Contain("SddlDonnees = 'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)';",
            "la même DACL que TrustedManifestRoot.BuildHardenedSecurity");
    }

    [Fact]
    public void La_desinstallation_ne_vide_plus_temoins_a_l_aveugle()
    {
        var iss = Iss();
        iss.Should().NotContain("Type: filesandordirs; Name: \"{commonappdata}");
        Corps(iss, "procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);").Should().Contain("SupprimerLesTemoins();");
        var suppression = Corps(iss, "procedure SupprimerLesTemoins();");
        suppression.Should().Contain("and $400", "une jonction n'est jamais suivie").And.Contain("DeleteFile(").And.Contain("RemoveDir(");
    }

    // Le manifeste n'est renommé « .rendu-… » que lorsque tout a été remis comme avant ; s'il reste, une origine n'a pas été
    // rendue (écriture refusée, cible disparue, helper qui n'a pas démarré). Le joueur l'apprend, au lieu d'une désinstallation
    // muette, et le dossier de réglages reste pour réessayer.
    [Fact]
    public void Une_desinstallation_qui_n_a_pas_tout_rendu_le_dit_au_joueur()
    {
        var iss = Iss();
        var post = Corps(iss, "procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");
        post.Should().Contain("FileExists(ExpandConstant('{commonappdata}\\DrDxt\\desired-state.manifest'))",
            "le manifeste resté en place est le signe qu'une origine n'a pas été rendue");
        post.Should().Contain("FileExists(ExpandConstant('{commonappdata}\\DrDxt\\" + DrDxt.ElevatedHelper.ModeDesinstallation.MarqueRenduIncomplet + "'))",
            "ce qui n'est pas au manifeste (journal, carte, plafond d'envoi, démarrage, délai) pose la marque du helper");
        post.Should().Contain("CustomMessage('RenduIncomplet')");
        iss.Should().Contain("en.RenduIncomplet=").And.Contain("fr.RenduIncomplet=", "le message existe dans les deux langues");
    }
}
