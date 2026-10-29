using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// La tâche « DrDXT helper » garde l'assistant administrateur pour la session : un processus élevé que chaque joueur choisit.
/// L'installeur ne la pose que case cochée (décochée par défaut), dossier protégé et empreintes relues, par le lanceur et
/// avec --app-path ; il la retire décochée, à la mise à jour avant toute copie, dossier non protégé, et à la désinstallation.
/// </summary>
public sealed class LAssistantDeSessionSurChoixTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss"));
    }

    private static string Bloc(string iss, string debut, string fin)
    {
        var i = iss.IndexOf(debut, StringComparison.Ordinal);
        i.Should().BePositive($"« {debut} » est dans le script");
        return iss[i..iss.IndexOf(fin, i + debut.Length, StringComparison.Ordinal)];
    }

    private const string Retrait = @"'/Delete /F /TN ""DrDXT helper""'";

    [Fact]
    public void La_case_est_decochee_par_defaut_et_dit_ce_qu_elle_lance()
    {
        var iss = Iss();

        iss.Should().MatchRegex(@"(?m)^Name: ""helperseance""; Description: ""\{cm:TacheHelper\}"";.*Flags: unchecked; Check: CompteDOrigineAdmin\s*$");
        iss.Should().Contain("fr.TacheHelper=Faire les essais d'Optimiser sans ouvrir DrDXT (lance l'assistant administrateur à l'ouverture de session)");
        iss.Should().Contain("en.TacheHelper=Run Optimize trials without opening DrDXT (starts the administrator helper when you sign in)");
    }

    [Fact]
    public void La_tache_n_est_posee_que_case_cochee_par_le_lanceur_avec_l_appelant_de_confiance()
    {
        var pose = Bloc(Iss(), "procedure PoserOuRetirerLeHelper();", "\nend;");

        Regex.Matches(pose, @"/Create ").Should().HaveCount(1);
        pose.IndexOf("if not WizardIsTaskSelected('helperseance') then", StringComparison.Ordinal)
            .Should().BeLessThan(pose.IndexOf("/Create ", StringComparison.Ordinal), "décochée, rien n'est créé");
        pose.Should().Contain(Retrait, "décochée, la tâche d'une installation précédente part");
        pose.Should().Contain(@"'\DrDxt.Lanceur.exe'", "le lanceur reconstruit l'environnement du processus élevé");
        pose.Should().Contain("<Arguments>DrDxt.ElevatedHelper.exe &quot;--app-path=").And.Contain("&quot; --service</Arguments>");
        pose.Should().Contain("<RunLevel>HighestAvailable</RunLevel>").And.Contain("<LogonType>InteractiveToken</LogonType>");
    }

    // Un administrateur qui valide l'UAC pour un joueur sans droits donne son jeton à l'installeur : GetUserNameString et
    // USERDOMAIN disent l'administrateur. La tâche irait à sa session, que le joueur ne joint pas (le tube n'admet que le
    // compte du helper, les Administrateurs et SYSTEM).
    [Fact]
    public void Le_principal_est_le_compte_de_la_session_et_non_celui_qui_valide_l_UAC()
    {
        var iss = Iss();
        var pose = Bloc(iss, "procedure PoserOuRetirerLeHelper();", "\nend;");
        var compte = Bloc(iss, "function CompteDOrigine(): String;", "\nend;");

        pose.Should().NotContain("GetUserNameString").And.NotContain("GetEnv('USERDOMAIN')");
        pose.Should().Contain("Compte := CompteDOrigine();").And.Contain("<UserId>' + Compte + '</UserId>");
        pose.IndexOf("(Compte = '')", StringComparison.Ordinal)
            .Should().BeLessThan(pose.IndexOf("/Create ", StringComparison.Ordinal), "sans compte dans la session, rien n'est posé");
        // WTSUserName (5) et WTSDomainName (7), de la session courante : rien que le compte écrive.
        compte.Should().Contain("LireLaSession(5)").And.Contain("LireLaSession(7)");
        iss.Should().Contain("WTSQuerySessionInformationW(0, $FFFFFFFF, Classe, Tampon, Octets)")
            .And.Contain("external 'WTSQuerySessionInformationW@wtsapi32.dll stdcall'");
    }

    // HighestAvailable n'élève pas un compte standard : la case n'existe que pour un compte d'origine administrateur, et une
    // demande en ligne de commande pour un compte standard se dit au journal.
    [Fact]
    public void La_case_n_est_proposee_qu_a_un_compte_d_origine_administrateur()
    {
        var iss = Iss();
        var admin = Bloc(iss, "function CompteDOrigineAdmin(): Boolean;", "\nend;");
        var pose = Bloc(iss, "procedure PoserOuRetirerLeHelper();", "\nend;");

        iss.Should().MatchRegex(@"(?m)^Name: ""helperseance"";.*; Check: CompteDOrigineAdmin\s*$");
        admin.Should().Contain("ExecAsOriginalUser(").And.Contain(@"whoami.exe') + '"" /groups").And.Contain("S-1-5-32-544");
        admin.Should().Contain("CompteDOrigineLu := 2;", "illisible = standard : la case ne s'offre pas sur un doute");
        admin.IndexOf("CompteDOrigineLu := 2;", StringComparison.Ordinal)
            .Should().BeLessThan(admin.IndexOf("CompteDOrigineLu := 1;", StringComparison.Ordinal));
        pose.Should().Contain("{param:TASKS|}").And.Contain("{param:MERGETASKS|}")
            .And.Contain("Log('helper : tache demandee en ligne de commande, refusee");
    }

    [Fact]
    public void La_tache_n_est_posee_que_dans_le_dossier_protege()
    {
        var etape = Bloc(Iss(), "procedure CurStepChanged(CurStep: TSetupStep);", "\nend;");
        var protege = etape[..etape.IndexOf("SuppressibleMsgBox(CustomMessage('ProtectionEchouee')", StringComparison.Ordinal)];
        var sinon = protege[protege.LastIndexOf("    else", StringComparison.Ordinal)..];

        protege.IndexOf("if ProtegerLeDossier() then", StringComparison.Ordinal)
            .Should().BeLessThan(protege.IndexOf("PoserOuRetirerLeHelper();", StringComparison.Ordinal));
        sinon.Should().Contain(Retrait, "dossier non protégé : la tâche d'avant ne relance pas un helper élevé sur ces fichiers");
    }

    [Fact]
    public void Mise_a_jour_et_desinstallation_retirent_la_tache_et_arretent_le_helper_baux_rendus()
    {
        var iss = Iss();
        var preparation = Bloc(iss, "function PrepareToInstall(", "\nend;");
        var desinstallation = Bloc(iss, "procedure CurUninstallStepChanged(", "if CurUninstallStep = usPostUninstall");
        var arret = Bloc(iss, "procedure LaisserLeHelperRendreSesBaux();", "\nend;");

        preparation.IndexOf(Retrait, StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(preparation.IndexOf("LaisserLeHelperRendreSesBaux();", StringComparison.Ordinal),
                "retirée avant l'arrêt du helper et la copie");
        desinstallation.IndexOf(Retrait, StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(desinstallation.IndexOf("LaisserLeHelperRendreSesBaux();", StringComparison.Ordinal));
        arret.IndexOf($"CreateMutex('{Program.MutexDeLInstalleur}');", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(arret.IndexOf("if not HelperVivant() then Exit;", StringComparison.Ordinal),
                "le helper en --service ne s'arrête que sur ce signal");
    }
}
