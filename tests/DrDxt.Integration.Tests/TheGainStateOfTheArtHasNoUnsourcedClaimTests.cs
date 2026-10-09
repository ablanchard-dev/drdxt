using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DrDxt.Integration.Tests;

/// <summary>
/// `docs/OU-VIT-LE-GAIN.md` repond a la question posee le 07/09/2026 : « on a un gain
/// ou pas ? ». Un document qui repond a CA finira cite dans une page de vente. Il porte donc la
/// meme regle mecanique que l'etat de l'art memoire : un chiffre sans source ne passe pas.
///
/// Et une regle de plus, propre a ce fichier : **la ligne G-05 doit rester UNKNOWN tant que
/// personne n'a mesure la part du parc concernee.** C'est le seul chiffre qui transformerait
/// « certains PC gagnent beaucoup » en « X % des PC gagnent Y », et c'est exactement celui qu'on
/// n'a pas. Un test qui le laisse se remplir sans source laisserait naitre la promesse.
/// </summary>
public class TheGainStateOfTheArtHasNoUnsourcedClaimTests
{
    private const int ClaimsAtGateWriting = 5;

    private static readonly Regex ClaimRow = new(@"^\|\s*(G-\d+)\s*\|(.+)\|\s*$", RegexOptions.Multiline);
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
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "OU-VIT-LE-GAIN.md"));

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
        claims.Should().HaveCountGreaterThanOrEqualTo(
            ClaimsAtGateWriting, "vider le tableau ne doit jamais etre une facon de passer ce controle");

        foreach (var c in claims.Where(c => ContainsDigit.IsMatch(c.Range)))
        {
            c.Source.Should().Contain("http", "la fourchette de {0} porte un chiffre", c.Id);
            IsoDate.IsMatch(c.ReadOn).Should().BeTrue(
                "{0} doit dire QUAND sa source a ete lue (AAAA-MM-JJ)", c.Id);
        }
    }

    [Fact]
    public void Une_ligne_qui_refuse_de_chiffrer_le_dit_dans_sa_classe()
    {
        foreach (var c in Claims().Where(c => c.Range == "UNKNOWN"))
            c.Class.Should().Be("NON CHIFFRE", "{0} ne chiffre pas : sa classe doit le dire", c.Id);
    }

    [Fact]
    public void La_part_du_parc_concernee_reste_UNKNOWN_tant_que_personne_ne_l_a_mesuree()
    {
        // La ligne qui garde la promesse. G-05 est le seul chiffre capable de transformer
        // « certains PC gagnent beaucoup » en « X % des PC gagnent Y » -- la phrase que toute page
        // de vente veut ecrire. Recherche du 07/09/2026 : aucune enquete chiffree n'existe, juste
        // des affirmations qualitatives. Le jour ou quelqu'un remplit cette case, il devra faire
        // passer ce test, donc apporter une source datee.
        var parc = Claims().Single(c => c.Id == "G-05");

        parc.Range.Should().Be("UNKNOWN",
            "aucune enquete chiffree du parc n'a ete trouvee : y mettre un nombre serait l'inventer");
    }

    [Fact]
    public void Aucune_ligne_ne_pretend_avoir_ete_mesuree_sur_la_machine_du_client()
    {
        foreach (var c in Claims())
            c.Class.Should().NotBe("MESURE ICI",
                "{0} vient d'une lecture externe, pas d'une mesure sur cette machine", c.Id);
    }
}
