using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Le score a quitte la vitrine le 05/09 : son plancher mathematique
// etait 47, il ne pouvait donc pas dire « cette machine est mal reglee ». Ce que
// le produit affirme depuis : N a corriger sur M lus, plus K non lus — trois
// entiers, tous des faits de lecture.
//
// Mesure du jour : deux chaines de score survivaient dans les ressources, et
// **l'une d'elles etait encore affichee**. Quand l'analyse echouait, le client
// lisait « Impossible de calculer ton score » — on lui refusait quelque chose que
// le produit ne fait plus. Le contrat du depot le dit deja des mots : ce qui
// ordonne encore un score le fait revenir a la premiere session distraite. Une
// ressource le fait revenir aussi surement qu'un contrat.
// -----------------------------------------------------------------------
public sealed class AucunTexteNePrometUnScoreTests
{
    // -------------------------------------------------------------------
    // 18/09/2026, deuxieme surface. Le contrat du depot previent : « un contrat qui
    // ordonne encore un score le fait revenir a la premiere session distraite ».
    //
    // Le cahier des charges porte bien une note de revision qui enterre le score --
    // mais elle vise nommement « les points 3 et 4 ». Deux autres passages
    // ordonnaient le meme score au client, §11 du cahier et le brief de recherche,
    // sans aucune marque. Un lecteur qui tombe dessus sans lire jusqu'a la note lit
    // une instruction vivante.
    //
    // On ne REECRIT pas ces documents : le cahier le dit lui-meme, « ce qu'il disait
    // a ete vrai, et savoir ce qu'on a cru compte autant que savoir ce qu'on sait ».
    // On exige seulement que chaque mention porte sa marque.
    // -------------------------------------------------------------------
    // 27/09/2026 : le brief de recherche a quitte le depot (synthese de travail, pas de la doc) ; le
    // cahier seul porte cinq mentions, le temoin ci-dessous reste au-dessus de son seuil.
    // 08/10/2026 (PUB2) : le cahier reste dans le dépôt privé ; la copie publique (tools/copie_publique.py) ne l'a pas, et
    // ce garde n'y a rien à garder.
    private static readonly string[] DocumentsDeReference =
        new[] { @"docs\CAHIER-DES-CHARGES.md" }.Where(d => File.Exists(Path.Combine(Racine().FullName, d))).ToArray();

    // Un score MONTRE au client. Le /100 calcule en interne n'est pas vise : il
    // existe toujours dans DrDxt.Detection, et c'est le montrer qui a ete abandonne.
    private static readonly Regex ScoreMontre = new(@"68\s*/\s*100|~?\s*90\s*/\s*100");

    [Fact]
    public void Le_crible_voit_bien_des_mentions_de_score_dans_les_documents()
    {
        // Temoin de contenu : le jour ou ces documents ne parleraient plus de score,
        // le garde ci-dessous serait vert sur du vide et personne ne le saurait.
        if (DocumentsDeReference.Length == 0) return;
        var lignes = DocumentsDeReference
            .SelectMany(d => File.ReadAllLines(Path.Combine(Racine().FullName, d)))
            .Count(l => ScoreMontre.IsMatch(l));

        lignes.Should().BeGreaterThan(2, "le cahier decrit encore le funnel d'origine");
    }

    [Fact]
    public void Chaque_score_montre_au_client_porte_sa_marque_de_revision()
    {
        var nues = new List<string>();
        foreach (var doc in DocumentsDeReference)
        {
            var lignes = File.ReadAllLines(Path.Combine(Racine().FullName, doc));
            for (var i = 0; i < lignes.Length; i++)
            {
                if (!ScoreMontre.IsMatch(lignes[i])) continue;
                // La note de revision elle-meme cite le score pour l'enterrer : elle est
                // sa propre marque. Un bloc cite (« > ») est cette note.
                if (lignes[i].TrimStart().StartsWith('>')) continue;
                if (lignes[i].Contains("n'existe plus")) continue;

                nues.Add($"{doc}:{i + 1}");
            }
        }

        nues.Should().BeEmpty(
            "ces lignes ordonnent encore de MONTRER un score au client, sans marque. " +
            "Le plancher mathematique du /100 etait 47 : il ne pouvait pas dire « cette " +
            "machine est mal reglee ». Une instruction non marquee le fait revenir");
    }

    private static readonly Regex Promesse =
        new(@"\bscores?\b|/\s*100\b|\bsur\s+100\b", RegexOptions.IgnoreCase);

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static IEnumerable<(string Fichier, string Cle, string Texte)> Chaines()
    {
        foreach (var nom in new[] { "Strings.resx", "Strings.fr.resx" })
        {
            var chemin = Path.Combine(Racine().FullName, "src", "DrDxt.Infrastructure", "Resources", nom);
            foreach (var d in XDocument.Load(chemin).Root!.Elements("data"))
            {
                var valeur = d.Element("value")?.Value ?? "";
                yield return (nom, d.Attribute("name")!.Value, valeur);
            }
        }
    }

    [Fact]
    public void Aucune_chaine_lue_par_le_client_ne_parle_de_score()
    {
        var fautives = Chaines()
            .Where(c => Promesse.IsMatch(c.Texte))
            .Select(c => $"{c.Fichier} :: {c.Cle} = « {c.Texte} »")
            .ToList();

        fautives.Should().BeEmpty(
            "le score a ete retire de tout ce que le client lit le 05/09/2026 : une chaine " +
            "qui en parle encore le ramene, et promet une mesure que le produit ne fait plus");
    }

    [Fact]
    public void Le_crible_lit_vraiment_les_ressources()
    {
        // Temoin : sur un fichier vide ou mal lu, le test precedent serait vert
        // pour toujours en annoncant une garantie sur rien.
        Chaines().Should().HaveCountGreaterThan(400);
    }

    [Fact]
    public void Le_crible_reconnait_bien_une_promesse_de_score()
    {
        // Temoin inverse : un motif qui ne matcherait rien rendrait le test muet.
        Promesse.IsMatch("Health score").Should().BeTrue();
        Promesse.IsMatch("Score de santé").Should().BeTrue();
        Promesse.IsMatch("72/100").Should().BeTrue();
        Promesse.IsMatch("3 à corriger sur 14 lus").Should().BeFalse("c'est un fait de lecture, pas une note");
    }

    [Fact]
    public void Le_message_d_echec_de_l_analyse_existe_dans_les_deux_langues()
    {
        // La garantie positive : retirer le mot « score » ne doit pas laisser
        // l'echec sans explication.
        var localise = new ResxLocalizer();
        foreach (var culture in new[] { "fr", "en" })
        {
            var texte = localise.Get("Scan.Error.Analyse", new CultureInfo(culture));
            texte.Should().NotBeNullOrWhiteSpace().And.NotBe("Scan.Error.Analyse", culture);
            Promesse.IsMatch(texte).Should().BeFalse(texte);
        }
    }
}
