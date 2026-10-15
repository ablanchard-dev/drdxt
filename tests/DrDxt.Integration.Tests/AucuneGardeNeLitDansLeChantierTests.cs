using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Les gardes de ce projet lisent les sources du depot. Elles le
// faisaient avec `SearchOption.AllDirectories`, en ecartant `bin`/`obj`/`.git`
// Apres l'enumeration -- donc apres y etre descendues. Pendant une execution de
// toute la solution, MSBuild cree et supprime des fichiers dans ces dossiers en
// continu, et une enumeration recursive dont un dossier disparait sous elle leve.
//
// Ce test interdit desormais le parcours brut dans ce projet : tout passe par
// `FichiersDuDepot.Sous`, qui elague avant de descendre.
//
// Ce que je n'affirme pas : que c'etait la cause du rouge intermittent. Ce
// rouge n'a jamais ete nomme (mon instrument l'a manque deux fois), et 25
// executions du projet seul n'ont rien redonne. Ce qui est sur et mesurable :
// ces gardes ouvraient un chantier en activite alors qu'elles cherchent des
// sources. On leur retire la seule raison qu'elles avaient d'y trebucher.
// -----------------------------------------------------------------------
public sealed class AucuneGardeNeLitDansLeChantierTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static string DossierDuProjet() =>
        Path.Combine(Racine(), "tests", "DrDxt.Integration.Tests");

    [Fact]
    public void Le_crible_lit_bien_les_sources_de_ce_projet()
    {
        // Temoin : sans lui, un dossier renomme rendrait le test suivant vert sur du vide.
        FichiersDuDepot.Sous(DossierDuProjet(), "*.cs").Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void Aucune_garde_n_enumere_le_depot_sans_elaguer()
    {
        var fautifs = new List<string>();

        foreach (var fichier in FichiersDuDepot.Sous(DossierDuProjet(), "*.cs"))
        {
            if (Path.GetFileName(fichier) is nameof(FichiersDuDepot) + ".cs"
                or nameof(AucuneGardeNeLitDansLeChantierTests) + ".cs") continue;

            if (File.ReadAllText(fichier).Contains("SearchOption.AllDirectories", StringComparison.Ordinal))
                fautifs.Add(Path.GetFileName(fichier));
        }

        fautifs.Should().BeEmpty(
            "un parcours recursif brut descend dans `obj/` et `bin/`, ou MSBuild ecrit pendant " +
            "que la suite tourne. `FichiersDuDepot.Sous` elague avant de descendre : une garde " +
            "qui cherche des sources n'a aucune raison d'ouvrir la sortie de build");
    }

    [Fact]
    public void L_elagueur_n_entre_jamais_dans_un_dossier_ignore()
    {
        // La garantie elle-meme, mesuree sur un faux depot : si le dossier ignore contenait
        // un fichier correspondant au motif, il ne doit pas sortir.
        var faux = Path.Combine(Path.GetTempPath(), "drdxt-elagage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(faux, "src"));
            Directory.CreateDirectory(Path.Combine(faux, "src", "obj"));
            File.WriteAllText(Path.Combine(faux, "src", "vrai.cs"), "//");
            File.WriteAllText(Path.Combine(faux, "src", "obj", "genere.cs"), "//");

            var vus = FichiersDuDepot.Sous(faux, "*.cs").Select(Path.GetFileName).ToList();

            vus.Should().ContainSingle().Which.Should().Be("vrai.cs");
        }
        finally
        {
            try { Directory.Delete(faux, recursive: true); } catch { /* le menage n'est pas le test */ }
        }
    }
}
