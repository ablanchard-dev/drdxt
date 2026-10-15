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
        entrees[0].Should().Contain(@"Filename: ""{app}\DrDxt.ElevatedHelper.exe""")
            .And.Contain($@"Parameters: ""{ModeDesinstallation.Argument}""")
            .And.Contain("skipifdoesntexist")
            .And.Contain("runhidden")
            .And.Contain("RunOnceId:")
            .And.NotContain("nowait", "la desinstallation attend le rendu avant de retirer le helper");
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
