using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. La journee a montre cinq fois la meme chose : **un test peut nommer
// une garantie sans la tenir**. Le cas extreme est detectable mecaniquement — un
// test qui n'affirme rien passe toujours, quel que soit le produit.
//
// Ce crible a ete ecrit puis affine quatre fois avant d'etre cru, parce que sa
// premiere version accusait 14 tests parfaitement sains :
//   • un helper local portait l'assertion  (`await Verrouille(...)`)
//   • `Received.InOrder(...)` ne s'ecrit pas `Received(`
//   • `throw new XunitException` s'ecrit en minuscule
// Un crible ne voit que ce qu'il sait voir, et un crible qui crie au loup finit
// desactive : c'est pour cela qu'il suit un niveau d'indirection et connait tout
// le vocabulaire d'assertion employe ici.
//
// Etat mesure a l'ecriture : **1 seul cas sur 3030**, et c'est un test dont
// l'assertion est le COMPILATEUR.
// -----------------------------------------------------------------------
public sealed class AucunTestNePasseSansRienProuverTests
{
    /// <summary>
    /// L'unique exception, nommee et datee : ce test lit deux membres d'enum et rien
    /// d'autre. S'ils disparaissent de l'interop PresentMon, **il ne compile plus** —
    /// l'assertion est le compilateur, pas une omission.
    /// </summary>
    private static readonly string[] ProuvesALaCompilation = ["Frametime_and_latency_metrics_exist"];

    private static readonly string[] Preuves =
        ["Should", "Assert.", "Throw", "throw ", "Verify(", "Received", "DidNotReceive"];

    private static readonly Regex Tests = new(
        @"\[(?:Fact|Theory)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?\w[\w<>\.]*\s+(\w+)\s*\([^)]*\)\s*\{",
        RegexOptions.Singleline);

    private static readonly Regex Methodes = new(
        @"(?:private|internal|protected|public|static)[\w\s<>,\[\]\.]*?\s(\w+)\s*\([^)]*\)\s*(?:=>|\{)");

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Corps(string source, int ouvrante)
    {
        int niveau = 0;
        for (int j = ouvrante; j < source.Length; j++)
        {
            if (source[j] == '{') niveau++;
            else if (source[j] == '}' && --niveau == 0) return source[ouvrante..j];
        }
        return source[ouvrante..];
    }

    private static bool Affirme(string corps) => Preuves.Any(corps.Contains);

    private static List<string> Creux()
    {
        var creux = new List<string>();
        var dossier = Path.Combine(Racine().FullName, "tests");

        foreach (var fichier in Directory.EnumerateFiles(dossier, "*.cs", SearchOption.AllDirectories))
        {
            if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                fichier.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var source = File.ReadAllText(fichier);

            // Les methodes du fichier qui portent elles-memes une assertion : un test
            // qui delegue a l'une d'elles prouve bel et bien quelque chose.
            var aides = Methodes.Matches(source)
                .Select(m => new { m.Groups[1].Value, Ouvrante = source.IndexOf('{', m.Index + m.Length - 1) })
                .Where(x => x.Ouvrante >= 0 && Affirme(Corps(source, x.Ouvrante)))
                .Select(x => x.Value)
                .ToHashSet();

            foreach (Match m in Tests.Matches(source))
            {
                var corps = Corps(source, m.Index + m.Length - 1);
                var nom = m.Groups[1].Value;

                if (Affirme(corps)) continue;
                if (aides.Any(a => Regex.IsMatch(corps, $@"\b{Regex.Escape(a)}\s*\("))) continue;
                if (ProuvesALaCompilation.Contains(nom)) continue;

                creux.Add($"{Path.GetFileName(fichier)} :: {nom}");
            }
        }
        return creux;
    }

    [Fact]
    public void Aucun_test_ne_passe_sans_rien_affirmer()
    {
        Creux().Should().BeEmpty(
            "un test sans assertion passe quoi qu'il arrive au produit : il occupe la " +
            "place d'une garantie sans en etre une");
    }

    [Fact]
    public void Le_crible_lit_vraiment_la_suite()
    {
        // Temoin indispensable : un motif qui ne trouverait aucun test rendrait le
        // test precedent vert a jamais, en annoncant une garantie sur le vide.
        var dossier = Path.Combine(Racine().FullName, "tests");
        var trouves = Directory.EnumerateFiles(dossier, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Sum(f => Tests.Matches(File.ReadAllText(f)).Count);

        trouves.Should().BeGreaterThan(2500, "la suite en comptait 3030 le jour de l'ecriture");
    }

    [Fact]
    public void L_exception_reste_une_exception_nommee()
    {
        // Une liste d'exceptions qui grossit est une regle qu'on a cesse de tenir.
        ProuvesALaCompilation.Should().HaveCount(1);
    }
}
