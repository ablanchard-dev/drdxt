using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// L'installeur parle anglais, et français quand Windows est en français : la langue de l'interface choisit, sans question.
// Chaque texte que le joueur lit (cases, groupes, état, refus, avertissement) passe par [CustomMessages], dans les deux
// langues ; un texte écrit en dur dans le code ne serait lu que dans une seule.
// -----------------------------------------------------------------------
public sealed class LInstalleurParleAnglaisEtFrancaisTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss")).Replace("\r\n", "\n");
    }

    private static Dictionary<string, string> Messages(string langue)
        => Regex.Matches(Iss(), $@"^{langue}\.(?<cle>\w+)=(?<t>.*)$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["cle"].Value, m => m.Groups["t"].Value);

    [Fact]
    public void L_anglais_est_la_langue_par_defaut_et_le_francais_est_propose()
    {
        var langues = Regex.Matches(Iss(), @"^Name:\s*""(?<l>\w+)"";\s*MessagesFile:", RegexOptions.Multiline)
            .Select(m => m.Groups["l"].Value).ToList();

        langues.Should().Equal(["en", "fr"], "la première langue sert quand celle de Windows n'est pas proposée");
        Iss().Should().Contain("ShowLanguageDialog=no");
    }

    [Fact]
    public void Chaque_message_existe_dans_les_deux_langues()
    {
        var en = Messages("en");
        var fr = Messages("fr");

        fr.Should().HaveCountGreaterThan(5, "témoin : le crible lit bien les messages");
        en.Keys.Should().BeEquivalentTo(fr.Keys);
        en.Values.Concat(fr.Values).Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t));
    }

    [Fact]
    public void Chaque_message_appele_est_defini()
    {
        var iss = Iss();
        var appeles = Regex.Matches(iss, @"\{cm:(?<cle>\w+)|CustomMessage\('(?<cle>\w+)'\)").Select(m => m.Groups["cle"].Value)
            .Where(c => c != "UninstallProgram") // message d'Inno lui-même, dans Default.isl et French.isl
            .ToList();

        appeles.Should().HaveCountGreaterThan(5, "témoin : les textes passent par les messages");
        appeles.Should().OnlyContain(c => Messages("en").ContainsKey(c));
    }

    [Fact]
    public void Aucun_texte_affiche_n_est_ecrit_en_dur()
    {
        var iss = Iss();

        Regex.Matches(iss, @"(?:Description|GroupDescription):\s*""(?<t>[^""]*)""").Select(m => m.Groups["t"].Value)
            .Should().NotBeEmpty().And.OnlyContain(t => t.StartsWith("{cm:"));
        iss.Should().NotMatchRegex(@"Caption\s*:=\s*'", "un libellé d'état ne se lirait qu'en une langue");
        iss.Should().NotMatchRegex(@"MsgBox\(\s*'", "un avertissement non plus");
        iss.Should().NotMatchRegex(@"Result\s*:=\s*'[^']", "ni le refus de PrepareToInstall");
    }

    [Fact]
    public void Le_mot_au_testeur_est_dans_la_langue_de_l_installation()
    {
        var lus = Regex.Matches(Iss(), @"^Source:.*isreadme.*$", RegexOptions.Multiline).Select(m => m.Value).ToList();

        lus.Should().HaveCount(2);
        lus.Should().ContainSingle(l => l.Contains("README-testeur.txt") && l.Contains("Languages: fr"));
        lus.Should().ContainSingle(l => l.Contains("README-tester.txt") && l.Contains("Languages: en"));
    }
}
