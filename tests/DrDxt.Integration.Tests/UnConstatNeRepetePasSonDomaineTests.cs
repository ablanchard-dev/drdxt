using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Pas de « Mémoire : overclock actif… » sous le titre « Mémoire », ni de « Mémoire · à savoir : Mémoire :
/// overclock… » dans le rapport. Un constat s'affiche toujours sous son
/// domaine, à l'écran comme dans le rapport : son texte ne le répète pas.
/// </summary>
public sealed class UnConstatNeRepetePasSonDomaineTests
{
    private static readonly Regex Prefixe = new(
        @"^(Mémoire|Réseau|Processeur|Latence|Stockage|Écran|GPU|CPU|Memory|Network|Storage|Display|Latency)\s*:");

    private static readonly Regex Entree = new(
        "<data name=\"(Finding\\.[^\"]+)\"[^>]*>\\s*<value>([^<]*)</value>");

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Theory]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.resx")]
    public void Aucun_constat_ne_commence_par_le_nom_de_son_domaine(string fichier)
    {
        // Témoin : le motif attrape la forme d'avant.
        Prefixe.IsMatch("Mémoire : overclock actif").Should().BeTrue();

        var texte = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.Infrastructure", "Resources", fichier));
        var constats = Entree.Matches(texte).Select(m => (Nom: m.Groups[1].Value, Valeur: m.Groups[2].Value)).ToList();
        constats.Should().HaveCountGreaterThan(20, "le fichier porte des dizaines de constats");

        constats.Where(c => Prefixe.IsMatch(c.Valeur)).Select(c => c.Nom)
            .Should().BeEmpty("le constat s'affiche sous son domaine : le répéter se lit deux fois");
    }
}
