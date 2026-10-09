using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// La coche verte qui ne verifie rien.
///
/// Trouve le 06/09/2026 en OUVRANT l'application compilee, pas en lisant le code : le pied de page
/// affiche en permanence « Mises a jour a jour » avec une coche verte, et le panneau assistant
/// affiche « En ligne » a cote de la phrase « tes donnees restent sur ton PC, rien n'est envoye ».
///
/// Les deux sont des CONSTANTES. `ScanViewModel.LabelFooterUpdates` rend une cle de ressource, rien
/// de plus : aucun etat, aucune verification, et `UpdateComposition` / `IUpdateEngine` n'apparaissent
/// nulle part dans le source de `DrDxt.App`. Le produit dit donc a chaque client qu'il est a jour
/// alors qu'il n'a jamais interroge quoi que ce soit -- et il ne le peut pas, faute d'URL de
/// deploiement (voir GATES).
///
/// C'est exactement le mensonge que ce produit existe pour ne pas faire, place sur son ecran
/// principal, en permanence.
///
/// ══ la forme de ce controle : il s'ouvre tout seul ══
/// Il n'interdit pas la phrase pour toujours. Il l'interdit **tant que rien ne la calcule**. Le jour
/// ou quelqu'un monte un moteur de mise a jour dans `DrDxt.App`, la condition tombe et la phrase
/// redevient permise -- parce qu'elle sera devenue vraie. Un gate qu'on ne peut pas satisfaire est
/// un gate qu'on desactive, et un gate desactive ment mieux qu'un gate absent (lecon du lot LS).
/// </summary>
public sealed class TheScreenMayNotClaimAStateItNeverComputesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    /// <summary>Le source de l'application, tests et binaires exclus.</summary>
    private static string AppSource()
    {
        var app = Path.Combine(RepoRoot(), "src", "DrDxt.App");
        return string.Join('\n', FichiersDuDepot.Sous(app, "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
    }

    private static string ResxValue(string file, string key)
    {
        var path = Path.Combine(RepoRoot(), "src", "DrDxt.Infrastructure", "Resources", file);
        var m = Regex.Match(File.ReadAllText(path),
            $"<data name=\"{Regex.Escape(key)}\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline);
        m.Success.Should().BeTrue("la cle {0} doit exister dans {1}", key, file);
        return m.Groups[1].Value;
    }

    [Fact]
    public void TheFooterMayNotSayTheAppIsUpToDateWhileNothingEverChecks()
    {
        // La condition qui rend ce controle honnete : il ne s'applique que tant que l'application
        // ne monte aucun moteur de mise a jour.
        var wired = AppSource().Contains("UpdateComposition") || AppSource().Contains("IUpdateEngine");
        if (wired) return;

        foreach (var file in new[] { "Strings.resx", "Strings.fr.resx" })
        {
            var v = ResxValue(file, "UI.Footer.Updates").Trim();

            // La premiere version de ce controle interdisait la sous-chaine « a jour ». Elle
            // rougissait sur « Mises a jour : jamais verifiees », qui est pourtant la phrase
            // HONNETE -- « Mises a jour » est le NOM du libelle, pas l'affirmation. L'outil avait
            // tort, pas le texte. Ce qui est interdit, c'est l'AFFIRMATION D'ETAT : la valeur ne
            // doit pas se TERMINER par « a jour », ni dire « up to date ».
            v.Should().NotEndWithEquivalentOf("à jour",
                "{0} affirme « {1} » sans qu'aucun moteur de mise a jour existe dans DrDxt.App", file, v);
            v.Should().NotContainEquivalentOf("up to date",
                "{0} affirme « {1} » sans qu'aucun moteur de mise a jour existe dans DrDxt.App", file, v);
        }
    }

    [Fact]
    public void TheAssistantMayNotSayItIsOnlineNextToNothingIsSent()
    {
        // « En ligne » a cote de « rien n'est envoye » : l'ecran se contredit lui-meme, et c'est
        // celui des deux qui rassure faussement qui doit partir.
        foreach (var file in new[] { "Strings.resx", "Strings.fr.resx" })
        {
            var v = ResxValue(file, "UI.Assistant.Online");

            v.Should().NotContainEquivalentOf("en ligne",
                "{0} affiche « {1} » alors que l'assistant est LOCAL et que l'ecran promet que rien " +
                "n'est envoye", file, v);
            v.Should().NotContainEquivalentOf("online",
                "{0} affiche « {1} » alors que l'assistant est LOCAL", file, v);
        }
    }

    [Fact]
    public void TheFooterMayNotWearAGreenCheckWhileNothingChecks()
    {
        // La coche verte est l'affirmation sous forme d'image : elle survit a toute correction du
        // TEXTE, et personne ne relit une icone. Vue le 06/09 a cote de « Mises a jour a jour »,
        // puis a cote de « jamais verifiees » -- ou elle contredisait la phrase qu'elle decorait.
        if (AppSource().Contains("UpdateComposition") || AppSource().Contains("IUpdateEngine")) return;

        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var footer = xaml.Substring(xaml.IndexOf("LabelFooterVersion", StringComparison.Ordinal));
        var block = footer.Substring(0, footer.IndexOf("LabelFooterUpdates", StringComparison.Ordinal));

        block.Should().NotContain("Source=Check",
            "le pied de page porte une coche verte alors qu'aucun moteur de mise a jour n'existe");
    }

    [Fact]
    public void TheComponentPanelMayNotTruncateAFactItWentToTheTroubleOfComputing()
    {
        // Vu a l'ecran le 06/09 : « 2 modules · 3800 MT/s (kit 4000) · 9,5 ns (kit CL... »
        // coupe en plein mot. La ligne memoire est la plus longue du panneau, et c'est justement
        // celle qui porte le plus de travail : la vitesse entrainee, le nominal du kit, et la
        // latence vraie en ns -- la seule grandeur comparable entre deux kits.
        //
        // Tronquer, c'est cacher. Un produit qui calcule un fait, l'etiquette pour dire d'ou il
        // sort, puis le coupe avec trois points, a fait tout le travail sauf le dernier pas.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
        // 08/10/2026 : le panneau lit les satellites (composant + état de son domaine), même gabarit, même règle.
        var start = xaml.IndexOf("ItemsSource=\"{Binding Satellites}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "le panneau des composants doit exister");
        var end = xaml.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
        var template = xaml.Substring(start, end - start);

        // Les commentaires XAML sont retires avant le controle. Sans ca, la phrase qui EXPLIQUE
        // pourquoi la troncature est interdite fait echouer le controle qui l'interdit -- vu dans
        // le tour meme ou ce test a ete ecrit. Un gate textuel se fait piéger par la prose qui
        // parle de lui.
        template = Regex.Replace(template, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        template.Should().NotContain("CharacterEllipsis",
            "le panneau des composants coupe une valeur qu'il a calculee au lieu de la faire passer "
            + "a la ligne");
    }

    /// <summary>Les trois verdicts que `VerdictText` peut rendre.</summary>
    private static readonly string[] VerdictKeys =
        { "UI.Verdict.Partial", "UI.Verdict.Clean", "UI.Verdict.Problems" };

    [Fact]
    public void TheResultHeadlineMayNotBeASentenceItsVerdictCannotFinish()
    {
        // Vu a l'ecran le 06/09 apres un vrai scan : « Cote config, votre PC est reglages a
        // corriger . » Le gabarit attend un ADJECTIF (« votre PC est ... »), et deux des trois
        // verdicts sont des GROUPES NOMINAUX depuis que le lot la a remplace le score.
        //
        // Le seul des trois qui se lit correctement est celui que la n'a pas touche. Personne ne
        // l'a vu parce que personne n'avait ouvert l'application : 2 834 tests, et la phrase
        // principale de l'ecran de resultat etait agrammaticale dans deux cas sur trois.
        //
        // La regle mecanique : une amorce de titre ne se termine pas par un verbe conjugue. Un
        // libelle qui ouvre une phrase impose une forme a tout ce qui le suit, et rien ne garantit
        // que les verdicts la respecteront encore demain.
        string[] copulas = { "est", "sont", "is", "are" };

        foreach (var file in new[] { "Strings.resx", "Strings.fr.resx" })
        foreach (var key in new[] { "UI.Result.HeadlineLead", "UI.Result.HeadlineLeadConfig" })
        {
            var lead = ResxValue(file, key).Trim().TrimEnd(':', ' ');
            var lastWord = lead.Split(' ', ',', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

            copulas.Should().NotContain(lastWord.ToLowerInvariant(),
                "{0}/{1} vaut « {2} » : une amorce qui finit par un verbe impose une forme aux "
                + "verdicts, et deux des trois n'y repondent pas", file, key, lead);
        }
    }

    [Fact]
    public void TheHeadlineMayNotPutASpaceBeforeItsFullStop()
    {
        // `<Run Text="."/>` sur sa propre ligne : WPF insere une espace entre deux Run, donc le
        // client lit « reglages a corriger . ». Visible sur la capture du 06/09.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var start = xaml.IndexOf("LabelResultHeadlineLead", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var block = xaml.Substring(start, 600);
        var end = block.IndexOf("</TextBlock>", StringComparison.Ordinal);
        block = block.Substring(0, end);

        block.Should().NotContain("Text=\".\"",
            "le point est un Run separe : WPF met une espace devant, et le client la voit");
    }

    [Fact]
    public void TheScoreEngineIsGoneFromWhatTheAppExposesAndSays()
    {
        // Le lot la du 05/09 disait : « Le moteur /100 : SUPPRIME, pas retreci. Un moteur dormant
        // + un contrat qui l'ordonne encore = un score qui revient a la premiere session
        // distraite. » Sa commande de cloture cherchait `ScoreNarrative`, les convertisseurs et
        // `/100`. Elle ne pouvait pas voir ce qui restait : cinq libelles `LabelScore*`, la
        // collection `SubScores`, et surtout `PopulateSubScores(scanResult.Breakdown, ...)`
        // **appele a chaque scan**. Le moteur dormait, mais il tournait.
        //
        // Ce controle regarde ce que le modele de vue EXPOSE et ce que les ressources DISENT,
        // c'est-a-dire les deux surfaces par lesquelles un score peut revenir a l'ecran.
        var vm = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DrDxt.App", "ViewModels", "ScanViewModel.cs"));
        var code = Regex.Replace(vm, @"//.*?$|/\*.*?\*/", string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline);

        foreach (var dead in new[] { "LabelScoreCaption", "LabelScoreOutOf", "LabelScorePerformance",
                                     "LabelScoreLatency", "LabelScoreStability", "SubScores",
                                     "PopulateSubScores" })
            code.Should().NotContain(dead,
                "`{0}` est un reste du moteur /100 que le lot LA a declare supprime", dead);

        foreach (var file in new[] { "Strings.resx", "Strings.fr.resx" })
        {
            var resx = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "DrDxt.Infrastructure", "Resources", file));
            foreach (var prefix in new[] { "<data name=\"UI.Score.", "<data name=\"Scan.Score." })
                resx.Should().NotContain(prefix,
                    "{0} porte encore des cles de score : une ressource qui existe finit par etre "
                    + "rebranchee", file);
        }
    }

}
