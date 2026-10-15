using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le README dit ce que le produit écrit sur la machine et ce qui la quitte. Une liste écrite à la main dérive dès qu'une
// feuille s'ajoute à la liste blanche du helper : ce test lit la liste blanche dans le code et
// exige que chaque valeur de registre écrivable soit nommée dans la section du README.
// -----------------------------------------------------------------------
public sealed class LeReadmeDitCeQueLeProduitEcritTests
{
    private static readonly Regex Feuille = new(
        @"new\(RegistryHiveKind\.\w+,\s*@""[^""]+"",\s*""([^""]+)"",\s*RegistryValueType\.\w+\)", RegexOptions.Compiled);

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static string Section(string readme, string titre)
    {
        var debut = readme.IndexOf(titre, StringComparison.Ordinal);
        debut.Should().BeGreaterThanOrEqualTo(0, $"le README doit avoir la section « {titre} »");
        var fin = readme.IndexOf("\n## ", debut + titre.Length, StringComparison.Ordinal);
        return fin < 0 ? readme[debut..] : readme[debut..fin];
    }

    // Les deux README : l'anglais est celui que GitHub montre, le français le suit ; aucun ne dérive sans que ça se voie.
    [Theory]
    [InlineData("README.fr.md", "## Ce que DrDXT écrit sur ta machine")]
    [InlineData("README.md", "## What DrDXT writes on your machine")]
    public void Chaque_valeur_de_registre_ecrivable_est_nommee_dans_le_README(string fichier, string titre)
    {
        var code = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.ElevatedHelper", "OpAllowlist.cs"));
        var valeurs = Feuille.Matches(code).Select(m => m.Groups[1].Value).Distinct().ToList();
        valeurs.Should().HaveCountGreaterThan(5, "témoin : le crible lit bien la liste blanche");

        var section = Section(File.ReadAllText(Path.Combine(Racine(), fichier)), titre);
        valeurs.Where(v => !section.Contains($"`{v}`", StringComparison.Ordinal))
            .Should().BeEmpty("chaque valeur que le helper peut écrire doit être nommée dans le README");
    }

    /// <summary>Le sens inverse. Trois valeurs retirées de la liste blanche restaient
    /// annoncées dans le README (« dix valeurs »), et le test ci-dessus ne pouvait pas le voir.</summary>
    [Theory]
    [InlineData("README.fr.md", "## Ce que DrDXT écrit sur ta machine", "- **Registre")]
    [InlineData("README.md", "## What DrDXT writes on your machine", "- **Registry")]
    public void Le_README_n_annonce_aucune_valeur_que_le_helper_ne_peut_plus_ecrire(string fichier, string titre, string puce)
    {
        var code = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.ElevatedHelper", "OpAllowlist.cs"));
        var valeurs = Feuille.Matches(code).Select(m => m.Groups[1].Value).ToHashSet();

        var section = Section(File.ReadAllText(Path.Combine(Racine(), fichier)), titre);
        var debut = section.IndexOf(puce, StringComparison.Ordinal);
        debut.Should().BeGreaterThanOrEqualTo(0, "témoin : la ligne du registre existe");
        var fin = section.IndexOf("\n- **", debut + 1, StringComparison.Ordinal);
        var ligne = fin < 0 ? section[debut..] : section[debut..fin];

        var annoncees = Regex.Matches(ligne, "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();
        annoncees.Should().HaveCountGreaterThan(5, "témoin : le crible lit bien la ligne du README");
        annoncees.Where(v => !valeurs.Contains(v))
            .Should().BeEmpty("le README ne doit nommer que ce que le helper peut encore écrire");
    }

    [Theory]
    [InlineData("README.fr.md", "## Ce qui quitte la machine", "mise à jour", "## Licence", "## Compiler")]
    [InlineData("README.md", "## What leaves the machine", "update", "## License", "## Build")]
    public void Le_README_dit_ce_qui_quitte_la_machine_et_sous_quelle_licence(string fichier, string sortie, string miseAJour,
        string licence, string compiler)
    {
        var readme = File.ReadAllText(Path.Combine(Racine(), fichier));
        Section(readme, sortie).Should().Contain(miseAJour);
        Section(readme, licence).Should().Contain("GPL-3.0").And.Contain("LICENSE");
        Section(readme, compiler).Should().Contain(".NET 10");
    }
}
