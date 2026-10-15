using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// `docs/LEVIERS-REPOS-ET-RESEAU.md` ouvre deux leviers dont aucun ne rend de FPS : la
/// consommation au repos et les economies d'energie de la carte reseau. C'est precisement le genre
/// de sujet ou une session pressee ecrit un chiffre trouve sur un forum. Meme regle mecanique que
/// pour la memoire : une fourchette chiffree porte sa source et la date de lecture, sinon elle
/// s'appelle `UNKNOWN`.
///
/// Et une regle propre a ce fichier : **toutes les lignes reseau doivent rester NON CHIFFRE.**
/// La recherche documentee n'a trouve aucun chiffre pour le JEU, seulement « des microsecondes par
/// reveil » et « negligeable sauf usages tres sensibles ». Notre plancher de bruit en jeu est de
/// 9,9 % : nous ne pourrons jamais mesurer un effet pareil, donc jamais le promettre. Le jour ou
/// quelqu'un remplit une de ces cases, il devra faire passer ce test.
/// </summary>
public class TheIdleAndNetworkStateOfTheArtTests
{
    private static readonly Regex ClaimRow = new(@"^\|\s*([RN]-\d+)\s*\|(.+)\|\s*$", RegexOptions.Multiline);
    private static readonly Regex ContainsDigit = new(@"\d");
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");

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

    private sealed record Claim(string Id, string Range, string Class, string Quality, string Source, string ReadOn);

    private static Claim[] Claims()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "LEVIERS-REPOS-ET-RESEAU.md"));

        return ClaimRow.Matches(doc).Select(m =>
        {
            var cells = m.Groups[2].Value.Split('|').Select(c => c.Trim()).ToArray();
            cells.Length.Should().Be(6, "la ligne {0} doit porter exactement 7 colonnes", m.Groups[1].Value);
            return new Claim(m.Groups[1].Value, cells[1], cells[2], cells[3], cells[4], cells[5]);
        }).ToArray();
    }

    [Fact]
    public void Toute_fourchette_chiffree_porte_sa_source_et_la_date_de_lecture()
    {
        var claims = Claims();
        claims.Should().HaveCountGreaterThanOrEqualTo(6, "vider le tableau ne doit pas passer ce controle");

        foreach (var c in claims.Where(c => ContainsDigit.IsMatch(c.Range)))
        {
            c.Source.Should().Contain("http", "la fourchette de {0} porte un chiffre", c.Id);
            IsoDate.IsMatch(c.ReadOn).Should().BeTrue("{0} doit dire QUAND sa source a ete lue", c.Id);
        }
    }

    [Fact]
    public void Une_ligne_qui_refuse_de_chiffrer_le_dit_dans_sa_classe()
    {
        foreach (var c in Claims().Where(c => c.Range == "UNKNOWN"))
            c.Class.Should().Be("NON CHIFFRE", "{0} ne chiffre pas : sa classe doit le dire", c.Id);
    }

    [Fact]
    public void Le_reseau_reste_entierement_NON_CHIFFRE_tant_qu_aucune_mesure_de_JEU_n_existe()
    {
        // La ligne qui garde la promesse. Un effet qui se compte en microsecondes par reveil est
        // plusieurs ordres de grandeur sous notre plancher de bruit in-game (9,9 %). Ecrire un
        // chiffre ici reviendrait a promettre ce qu'aucun de nos instruments ne pourra verifier.
        var reseau = Claims().Where(c => c.Id.StartsWith("N-", StringComparison.Ordinal)).ToArray();

        reseau.Should().NotBeEmpty();
        reseau.Should().OnlyContain(c => c.Range == "UNKNOWN",
            "aucune source ne chiffre l'effet en JEU de ces reglages");
    }

    [Fact]
    public void Aucune_ligne_ne_pretend_avoir_ete_mesuree_sur_la_machine_du_client()
    {
        foreach (var c in Claims())
            c.Class.Should().NotBe("MESURE ICI",
                "{0} vient d'une lecture externe, pas d'une mesure sur cette machine", c.Id);
    }
}
