using System.Text.RegularExpressions;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// La desinstallation REND les reglages : l'installeur lance
/// `DrDxt.ElevatedHelper.exe --uninstall-revert` depuis `[UninstallRun]`, qui tourne en premier dans
/// `PerformUninstall`, apres `usUninstall` (App et helper tues) et avant le retrait des fichiers. La ligne
/// est relue ici : l'argument doit etre celui que le helper reconnait, et rien ne doit rendre l'appel
/// bloquant ou fatal pour la desinstallation.
/// </summary>
public sealed class DesinstallationInstalleurTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    // Un « %ProgramData% » littéral, déposé par un composant de Windows quand l'App élevée tournait sans cette variable,
    // bloquait la suppression du dossier : Inno « Failed to delete directory (145) ». Seul ce résidu part, puis le dossier
    // s'il est vide. Jamais {app} lui-même en filesandordirs : une installation d'avant la page de choix fermée, ou un
    // /DIR=, peut l'avoir mis dans un dossier du joueur, que la désinstallation viderait.
    [Fact]
    public void Seul_le_residu_connu_part_jamais_le_dossier_d_installation_en_entier()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        var section = Regex.Match(iss, @"(?ms)^\[UninstallDelete\]\s*$(.*?)(?=^\[)").Groups[1].Value;
        // Le fichier exact, puis chaque dossier parent s'il est vide, du plus profond au dossier de DrDXT : Inno traite
        // [UninstallDelete] dans l'ordre du script. Jamais filesandordirs sous {app} : il suivrait une jonction.
        string[] dossiers = [@"{app}\%ProgramData%\Microsoft\Windows\UUS\State", @"{app}\%ProgramData%\Microsoft\Windows\UUS",
            @"{app}\%ProgramData%\Microsoft\Windows", @"{app}\%ProgramData%\Microsoft", @"{app}\%ProgramData%", "{app}"];
        var attendu = new[] { $@"Type: files; Name: ""{dossiers[0]}\_active.uusver""" }
            .Concat(dossiers.Select(d => $@"Type: dirifempty; Name: ""{d}""")).ToArray();
        section.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("Type:", StringComparison.Ordinal) && l.Contains("{app}", StringComparison.Ordinal))
            .Should().Equal(attendu);
        section.Should().NotMatchRegex(@"(?mi)^Type: filesandordirs; Name: ""\{app\}");
    }

    // Ce que le helper a écrit pour le service sous %ProgramData%\DrDxt part avec lui. Laissée, une pause du maintien
    // survivait à une réinstallation sans geste du joueur. Le plafond est rendu avant (le service est arrêté au
    // désinstallement, puis [UninstallRun] lance RemoveUploadCapOp) : [UninstallDelete] tourne en dernier. Le fichier exact,
    // jamais le dossier : la racine porte aussi la clé et le manifeste de la machine.
    [Fact]
    public void Les_reglages_du_service_partent_a_la_desinstallation()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        var section = Regex.Match(iss, @"(?ms)^\[UninstallDelete\]\s*$(.*?)(?=^\[)").Groups[1].Value;
        var lignes = section.Split('\n').Select(l => l.Trim()).ToList();

        lignes.Should().Contain(@"Type: files; Name: ""{commonappdata}\DrDxt\pause-du-maintien""");
        lignes.Should().Contain(@"Type: files; Name: ""{commonappdata}\DrDxt\plafond-envoi.json""");
    }

    [Fact]
    public void Le_dossier_d_installation_ne_se_choisit_pas()
    {
        // La page de choix du dossier était affichée. Un dossier créé à
        // la racine (C:\DrDXT) hérite de C:\ : les utilisateurs authentifiés y modifient les fichiers.
        // Le helper, lancé élevé à la demande et par le désinstalleur, y devenait remplaçable sans droits.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        iss.Should().MatchRegex(@"(?m)^DisableDirPage=yes\s*$");
        iss.Should().MatchRegex(@"(?m)^DefaultDirName=\{autopf\}\\DrDXT\s*$");
    }

    [Fact]
    public void La_desinstallation_lance_le_rendu_du_helper_sans_pouvoir_la_bloquer()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        var section = Regex.Match(iss, @"^\[UninstallRun\]\s*$(?<corps>.*?)^\[",
            RegexOptions.Multiline | RegexOptions.Singleline);
        section.Success.Should().BeTrue("le rendu des reglages passe par [UninstallRun]");

        var entrees = section.Groups["corps"].Value.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Filename:", StringComparison.Ordinal))
            .ToList();

        entrees.Should().ContainSingle();
        // Par le lanceur natif, comme tout lancement eleve : lance directement, le helper .NET heritait de
        // HKCU\Environment, ou un programme du compte pose de quoi charger sa DLL (CORECLR_PROFILER).
        entrees[0].Should().Contain(@"Filename: ""{app}\DrDxt.Lanceur.exe""")
            .And.Contain($@"Parameters: ""DrDxt.ElevatedHelper.exe {ModeDesinstallation.Argument}""")
            .And.Contain("skipifdoesntexist")
            .And.Contain("runhidden")
            .And.Contain("RunOnceId:")
            .And.NotContain("nowait", "la desinstallation attend le rendu avant de retirer le helper");
    }

    [Fact]
    public void Une_mise_a_jour_arrete_tout_ce_qui_verrouille_les_fichiers_remplaces()
    {
        // Le helper (lancé élevé par l'App) et notre service PresentMon tiennent leurs .exe ouverts dans {app} :
        // une mise à jour qui ne les arrête pas bute sur des fichiers verrouillés.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        var corps = Regex.Match(iss, @"^function PrepareToInstall\(.*?^end;", RegexOptions.Multiline | RegexOptions.Singleline);
        corps.Success.Should().BeTrue("témoin : la préparation de la mise à jour est relue");

        corps.Value.Should().Contain("'/IM DrDxt.ElevatedHelper.exe /F'");
        // Seulement le nôtre, décidé par la règle partagée (PresentMonEstANous) : celui d'un autre produit continue.
        Regex.IsMatch(corps.Value, @"if PresentMonEstANous\(\) then\s+Exec\([^;]*'stop \{#PmService\}'")
            .Should().BeTrue("l'arrêt de PresentMon est gardé par PresentMonEstANous");
    }

    [Fact]
    public void L_installeur_ne_lance_aucun_outil_par_son_nom_seul()
    {
        // L'installeur tourne en administrateur : un « sc.exe » sans chemin peut etre cherche hors de
        // System32 (dossier courant). Chaque Exec du [Code] doit partir d'une constante de dossier.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));
        var appels = Regex.Matches(iss, @"\bExec\(\s*(?<cible>[^,]+),").Select(m => m.Groups["cible"].Value.Trim()).ToList();

        appels.Should().NotBeEmpty("temoin : l'installeur arrete et retire le service par Exec");
        appels.Should().OnlyContain(c => c.StartsWith("ExpandConstant('{sys}\\", StringComparison.Ordinal),
            "un outil lance en administrateur se nomme par son chemin System32");
    }
}
