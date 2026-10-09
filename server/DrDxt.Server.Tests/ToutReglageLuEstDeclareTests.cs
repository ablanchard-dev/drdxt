using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Server.Tests;

// -----------------------------------------------------------------------
// 19/09/2026. UN REGLAGE QUE PERSONNE NE PEUT DEVINER NE SERA PAS POSE.
//
// `appsettings.json` est ce qu'un deployeur lit pour savoir quoi configurer. Il porte
// meme une ligne qui enumere les variables d'environnement a fournir -- secrets compris,
// avec la regle « NEVER commit real secrets ». C'est le bon reflexe, et c'est le document
// qui fait foi.
//
// La derive : le 19/09, les compteurs de `/health` ont ete fermes
// derriere `Health:Token` -- ils publiaient le nombre de commandes et le fait que les
// remboursements ne revoquent rien. Le correctif etait bon, mais declare NULLE PART.
//
// Consequence, et elle est silencieuse : quelqu'un qui deploie lit ce fichier, pose les
// quatre variables citees, et les compteurs restent fermes POUR TOUJOURS. C'est le bon
// defaut -- ferme -- mais cela retire sans un mot la mesure que ce tour-la avait tenu a
// garder. Le mainteneur regarderait `/health`, verrait un `status` seul, et conclurait qu'on a
// supprime les compteurs.
//
// Meme chose pour le canal de mise a jour : `Updates:<Canal>:{Version,Url,Sha256}` est lu
// par la route du manifeste, refuse s'il est incoherent (19/09 aussi), et n'apparait dans
// aucun fichier de configuration. On ne peut pas publier une version sans deviner trois
// noms de clefs.
//
// Ce crible lit les DEUX formes -- `config["Section:Clef"]` et les `SectionName` des
// classes d'options -- et exige que chaque racine soit declaree.
// -----------------------------------------------------------------------
public sealed class ToutReglageLuEstDeclareTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDxt.Server.sln"))) d = d.Parent;
        return d!;
    }

    private static string Source() =>
        string.Join("\n", Directory
            .EnumerateFiles(Path.Combine(Racine().FullName, "DrDxt.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

    private static JsonElement AppSettings()
    {
        var chemin = Path.Combine(Racine().FullName, "DrDxt.Server", "appsettings.json");
        return JsonDocument.Parse(File.ReadAllText(chemin)).RootElement;
    }

    /// <summary>Les sections que le code consulte, sous leurs deux formes d'ecriture.</summary>
    private static SortedSet<string> SectionsLues()
    {
        var source = Source();
        var racines = new SortedSet<string>(StringComparer.Ordinal);

        // 1. `config["Section:Clef"]` — y compris une racine suivie d'une concatenation.
        foreach (Match m in Regex.Matches(source, @"[Cc]onfig(?:uration)?\[""([A-Za-z]+):"))
            racines.Add(m.Groups[1].Value);

        // 2. `GetSection("Updates:" + quelqueChose)` — la clef est calculee, la racine non.
        foreach (Match m in Regex.Matches(source, @"GetSection\(""([A-Za-z]+):"))
            racines.Add(m.Groups[1].Value);

        // 3. `GetSection(XOptions.SectionName)` — le nom vit dans la classe d'options.
        foreach (Match m in Regex.Matches(source, @"GetSection\((\w+)Options\.SectionName\)"))
        {
            var nom = Regex.Match(source, $@"class {m.Groups[1].Value}Options.*?SectionName\s*=\s*""(\w+)""",
                                  RegexOptions.Singleline);
            if (nom.Success) racines.Add(nom.Groups[1].Value);
        }

        return racines;
    }

    [Fact]
    public void Le_crible_voit_bien_les_deux_formes()
    {
        // Temoin : sans lui, un motif casse rendrait le test suivant vert sur du vide.
        var lues = SectionsLues();

        lues.Should().Contain("Updates", "la forme `GetSection(\"Section:\" + ...)` doit etre vue");
        lues.Should().Contain("ManifestSigning", "et la forme `XOptions.SectionName` aussi");
    }

    [Fact]
    public void Chaque_section_lue_est_declaree_dans_appsettings()
    {
        var declarees = AppSettings().EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var manquantes = SectionsLues().Where(s => !declarees.Contains(s)).ToArray();

        manquantes.Should().BeEmpty(
            "un reglage qu'un deployeur ne peut pas deviner ne sera pas pose, et son absence "
            + "ne se verra pas : elle ferme une porte en silence");
    }

    [Fact]
    public void La_ligne_des_SECRETS_cite_la_graine_de_signature()
    {
        // Contrepoids : declarer la section ne suffit pas pour un secret. Celui-ci ne doit
        // jamais etre commite, il passe donc par l'environnement -- et la seule phrase qui
        // le dise est cette ligne-la.
        var commentaire = AppSettings().GetProperty("_comment_secrets").GetString()!;

        commentaire.Should().Contain("ManifestSigning__Ed25519PrivateSeedBase64",
            "c'est la ligne que lit celui qui deploie : elle enumere ce qui arrive par "
            + "l'environnement, et un secret absent de cette liste n'est pose par personne");
    }

    [Fact]
    public void Un_secret_reste_un_PLACEHOLDER_vide_dans_le_fichier()
    {
        // Second contrepoids : rien de ce qu'on ajoute ici ne doit porter de vraie valeur.
        foreach (var (section, clef) in new[]
                 {
                     ("ManifestSigning", "Ed25519PrivateSeedBase64"),
                 })
        {
            AppSettings().GetProperty(section).GetProperty(clef).GetString()
                .Should().BeEmpty($"{section}:{clef} est un secret : le fichier ne porte qu'un vide");
        }
    }
}
