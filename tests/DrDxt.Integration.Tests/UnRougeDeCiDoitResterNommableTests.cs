using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. `dotnet test DrDXT.sln` a rendu un rouge sur 4 366, dans ce
// projet-ci. Le meme projet lance seul : 111 verts. Huit relances de la
// solution entiere, dont une avec un build en parallele pour charger la
// machine : vertes. Le defaut existe, il est rare, et il est lie a la charge.
//
// Je n'ai pas pu le nommer. Pas parce qu'il se cache : parce que **rien ne
// garde la trace d'un echec**. `dotnet test` n'ecrit aucun rapport ici, ni en
// local ni en CI ; la seule trace est la sortie console du job, qui part avec
// le log. Un flake qui tombe une fois sur huit et ne laisse rien derriere lui
// est un flake qu'on relance sans lire -- puis une CI rouge qu'on ignore.
//
// Ce fichier ne corrige pas le flake, il rend le PROCHAIN nommable. C'est le
// seul geste qui ne demande pas de savoir lequel c'est.
//
// Ce qu'il ne fait pas : interdire les delais muraux dans les tests. Mesure
// faite avant d'ecrire quoi que ce soit : 19 emplois dans 12 fichiers, la
// plupart legitimes (attendre un processus). Un crible qui les accuserait tous
// crierait au loup et serait desactive la semaine suivante.
// -----------------------------------------------------------------------
public sealed class UnRougeDeCiDoitResterNommableTests
{
    private static string Workflow()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, ".github", "workflows", "ci.yml"));
    }

    /// <summary>Les etapes du workflow, une par `- name:`, options multi-lignes comprises.</summary>
    private static IEnumerable<string> Etapes()
        => Regex.Split(Workflow(), @"(?m)^\s*-\s+name:").Skip(1);

    /// <summary>Celles qui lancent des tests : ce sont elles qui peuvent rougir.</summary>
    private static List<string> EtapesDeTest()
        => Etapes().Where(e => e.Contains("dotnet test")).ToList();

    [Fact]
    public void Le_crible_voit_bien_les_etapes_qui_lancent_des_tests()
    {
        // Temoin de contenu : sans lui, un workflow renomme rendrait tout vert sur rien.
        var etapes = EtapesDeTest();

        etapes.Should().HaveCountGreaterThan(3, "la CI porte plusieurs portes dures");
        etapes.Should().Contain(e => e.Contains("DrDXT.sln"), "dont la suite complete");
    }

    [Fact]
    public void Chaque_etape_de_test_laisse_un_rapport()
    {
        var muettes = EtapesDeTest()
            .Where(e => !e.Contains("--logger"))
            .Select(e => e.Split('\n')[0].Trim())
            .ToList();

        muettes.Should().BeEmpty(
            "sans rapport, un rouge intermittent ne laisse que la sortie console du job, " +
            "qui part avec le log. On relance, et la preuve est perdue -- c'est exactement " +
            "ce qui m'est arrive le 18/09 sur 1 rouge parmi 4 366");
    }

    [Fact]
    public void Les_rapports_survivent_a_un_job_rouge()
    {
        // Un rapport ecrit dans un runner qui s'eteint ne sert a personne : ce qui compte,
        // c'est qu'il soit televerse PRECISEMENT quand ca echoue.
        var workflow = Workflow();

        workflow.Should().Contain("upload-artifact",
            "le rapport doit quitter le runner, sinon il meurt avec lui");
        Regex.IsMatch(workflow, @"if:\s*(failure\(\)|always\(\))")
            .Should().BeTrue("il doit etre televerse meme quand -- surtout quand -- le job est rouge");
    }
}
