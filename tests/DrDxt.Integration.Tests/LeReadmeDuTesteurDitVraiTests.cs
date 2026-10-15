using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// `installer/README-testeur.txt` est le seul texte qu'un testeur lit
// avant de lancer DrDXT. Quarante lignes, six affirmations verifiables sur le
// produit, et aucun test : elles n'etaient tenues que par la memoire de celui
// qui les a ecrites.
//
// Deux d'entre elles ne sont pas des arguments de vente.
//
// « L'installeur enregistre le service en mode MANUEL (demarre a la demande, pas
// au demarrage de Windows) » : c'est ce qui distingue un outil d'optimisation
// d'un bolt-on toujours allume, sur un PC de joueur. Et le basculer est d'autant
// plus probable que le .iss porte deja, EN COMMENTAIRE, le modele d'un service
// `start= auto` pour la version vendue. Le jour ou quelqu'un suit ce modele, le
// README continuera de promettre le contraire.
//
// « DrDXT ne s'injecte jamais dans le jeu, ne pose aucun overlay. Sans risque
// pour ton compte. » C'est la phrase qui engage le compte Warzone de quelqu'un.
// Elle ne peut pas reposer sur une intention.
//
// Mesure du jour, sans defaut trouve : le service est cree et reconfigure en
// `start= demand`, il est arrete puis supprime a la desinstallation, et aucune
// API d'injection n'existe dans le livre. Bruyance du crible mesuree avant de
// l'ecrire : zero accusation.
// -----------------------------------------------------------------------
public sealed class LeReadmeDuTesteurDitVraiTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Lire(params string[] morceaux)
        => File.ReadAllText(Path.Combine(Racine().FullName, Path.Combine(morceaux)));

    private static string Readme() => Lire("installer", "README-testeur.txt");

    /// <summary>Le script sans ses commentaires : un modele decrit en prose n'est pas une ligne executee.</summary>
    /// <remarks>
    /// Sans ce tri, le crible accuserait le `start= auto` que le .iss porte en commentaire pour
    /// decrire le service du reconciliateur — une fausse accusation, et un crible qui accuse a
    /// tort finit desactive.
    ///
    /// Deuxieme essai. Le premier retirait tout bloc entre accolades : il mangeait aussi
    /// `{#PmService}` et `{app}`, qui sont une directive de preprocesseur et une constante,
    /// pas des commentaires. Le crible a alors accuse une desinstallation parfaitement
    /// correcte. En Inno, un commentaire de code s'ouvre sur `{` suivi d'un blanc.
    /// </remarks>
    private static string IssExecutable()
    {
        var sans = Regex.Replace(Lire("installer", "DrDXT.iss"), @"\{\s[^{}]*\}", " ", RegexOptions.Singleline);
        return string.Join('\n', sans.Split('\n').Where(l => !l.TrimStart().StartsWith(';')));
    }

    [Fact]
    public void Le_crible_lit_bien_le_readme_et_le_script()
    {
        // Temoin de contenu : sans lui, un fichier deplace rendrait tout vert sur du vide.
        Readme().Should().Contain("PresentMonService").And.Contain("ANTI-CHEAT");
        IssExecutable().Should().Contain("sc.exe").And.NotBeEmpty();
    }

    [Fact]
    public void Le_tri_des_commentaires_garde_le_code_et_jette_la_prose()
    {
        // Temoin inverse : une phrase qui n'existe qu'en commentaire disparait, la creation reelle reste.
        var iss = Lire("installer", "DrDXT.iss");
        iss.Should().Contain("Ne PAS ecrire une constante entre accolades ici");
        IssExecutable().Should().NotContain("Ne PAS ecrire une constante entre accolades ici", "le tri jette la prose");
        IssExecutable().Should().Contain("start= demand", "alors que la creation reelle, elle, est executee");
    }

    [Fact]
    public void Le_service_de_mesure_reste_en_demarrage_manuel()
    {
        Readme().Should().Contain("mode manuel", "c'est ce que le testeur lit avant d'installer");

        // Le script cree aussi, sur choix, le service de demarrage. La promesse du README
        // porte sur le service de MESURE : on lit donc les modes des seuls appels sc qui le visent.
        var modes = Regex.Matches(IssExecutable(), @"'(?:create|config) \{#PmService\}[^;]*?start=\s*([a-z]+)", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        modes.Should().HaveCount(2, "creation et repointage : le crible doit lire les deux");
        modes.Should().AllBe("demand",
            "le README promet un service qui ne demarre PAS avec Windows. Un service en " +
            "demarrage automatique sur un PC de joueur, c'est exactement le bolt-on toujours " +
            "allume que ce produit dit ne pas etre");
    }

    [Fact]
    public void Aucun_service_ne_demarre_avec_Windows_sans_la_case()
    {
        Readme().Should().Contain("decochee par defaut");

        var iss = IssExecutable();
        var auto = Regex.Matches(iss, @"start=\s*auto").Select(m => m.Index).ToList();
        auto.Should().NotBeEmpty("temoin : le service de demarrage existe bien");

        var garde = iss.IndexOf("if WizardIsTaskSelected('reconciler') then", StringComparison.Ordinal);
        garde.Should().BePositive();
        var sinon = iss.IndexOf("  else", garde, StringComparison.Ordinal);
        auto.Should().OnlyContain(i => i > garde && i < sinon,
            "un demarrage automatique n'existe que dans la branche de la case cochee");
    }

    [Fact]
    public void La_desinstallation_retire_bien_le_service()
    {
        Readme().Should().Contain("La desinstallation le retire");

        var iss = IssExecutable();
        iss.Should().Contain("stop {#PmService}", "on arrete avant de supprimer");
        iss.Should().Contain("delete {#PmService}",
            "sinon un service fantome reste enregistre sur la machine apres une desinstallation " +
            "que le README annonce complete");
    }

    [Fact]
    public void Rien_dans_le_livre_ne_sait_s_injecter_dans_un_autre_processus()
    {
        // La promesse la plus lourde du document : elle engage le compte Warzone de quelqu'un.
        Readme().Should().Contain("ne s'injecte JAMAIS").And.Contain("aucun overlay");

        var interdites = new Regex(
            "CreateRemoteThread|WriteProcessMemory|SetWindowsHookEx|VirtualAllocEx|QueueUserAPC|NtMapViewOfSection");

        var sources = FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs").Select(p => new FileInfo(p))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        sources.Should().HaveCountGreaterThan(100, "temoin de contenu : le crible doit lire le livre");

        var fautives = sources
            .Where(f => interdites.IsMatch(File.ReadAllText(f.FullName)))
            .Select(f => f.Name)
            .ToList();

        fautives.Should().BeEmpty(
            "le README promet « sans risque pour ton compte ». Une seule de ces API dans le " +
            "produit, et cette phrase engage le compte d'un joueur sur une intention");
    }
}
