using DrDxt.Bios;
using FluentAssertions;
using Xunit;

namespace DrDxt.Bios.Tests;

/// <summary>
/// L'en-tete de <see cref="GuideGenerator"/> porte cinq regles imperatives. Mesure du 05/09/2026 :
/// quatre etaient tenues par un test, et la cinquieme — la plus grave — n'etait tenue par rien :
///
///     « NEVER writes the BIOS — a guide is text/steps for a human. »
///
/// Elle tient aujourd'hui, et de la meilleure facon : `DrDxt.Bios` ne reference que `DrDxt.Core`
/// et les abstractions d'injection. L'assembly ne PEUT pas ecrire — pas de canal privilegie, pas
/// d'API firmware, rien. C'est de la non-nuisance par construction.
///
/// Mais une reference de projet s'ajoute en une ligne, six mois plus tard, par quelqu'un qui
/// voulait juste « rendre le guide capable de verifier tout seul ». Ce jour-la, le composant qui
/// envoie l'utilisateur dans son BIOS gagnerait le droit d'y ecrire, et aucun test ne bougerait.
///
/// Ce controle est cette ligne rouge. Quand il tombe : ce n'est pas le test qu'on corrige.
///
/// ══ ce qu'il attrape exactement, mesure plutot qu'affirme (05/09/2026) ══
///
/// `GetReferencedAssemblies` ne liste que les assemblies reellement UTILISEES : le compilateur
/// retire du manifeste une reference a laquelle aucun type ne touche. Verifie dans les deux sens :
///   • ajouter `<ProjectReference>` vers `DrDxt.Detection` sans s'en servir -> ce gate reste VERT ;
///   • y toucher un seul type -> il devient ROUGE.
///
/// Ce n'est pas une faiblesse, c'est la bonne frontiere : une reference dont personne ne se sert
/// n'ecrit rien. Ce qui est garde ici, c'est l'USAGE, et c'est l'usage qui peut nuire.
///
/// (Ecrit apres avoir corrige un commentaire de garde qui expliquait le mauvais mecanisme : une
/// explication fausse est la maniere dont le suivant casse la garantie en croyant la respecter.)
/// </summary>
public sealed class TheGuideNeverWritesTheBiosGateTests
{
    /// <summary>Le seul assembly maison que le generateur de guide a le droit de connaitre.</summary>
    private const string Allowed = "DrDxt.Core";

    private static List<string> ForbiddenAmong(IEnumerable<string> referenced) =>
        referenced
            .Where(n => n.StartsWith("DrDxt.", StringComparison.Ordinal))
            .Where(n => !string.Equals(n, Allowed, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_guide_assembly_cannot_reach_any_write_path()
    {
        var referenced = typeof(GuideGenerator).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // Sans ceci, un chargement rate rendrait la meme couleur qu'un depot sain.
        referenced.Should().Contain(Allowed, "le generateur s'appuie bien sur les contrats du coeur");

        ForbiddenAmong(referenced).Should().BeEmpty(
            "un guide est du texte pour un humain ; l'assembly qui envoie l'utilisateur dans son "
            + "BIOS ne doit pas pouvoir y ecrire, et c'est la reference de projet qui le garantit");
    }

    [Fact]
    public void The_detector_would_actually_catch_a_new_reference()
    {
        // Controle POSITIF sur une liste synthetique : il prouve que le detecteur sait dire non.
        ForbiddenAmong(["DrDxt.Core", "System.Runtime", "DrDxt.ElevatedHelper"])
            .Should().ContainSingle().Which.Should().Be("DrDxt.ElevatedHelper");
    }
}
