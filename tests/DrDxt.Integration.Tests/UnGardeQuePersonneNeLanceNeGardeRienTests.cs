using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Le depot porte quatre gates en Python sous `tools/gates/` :
//
//   ages.py             recalcule l'age de chaque ligne de GATES.md
//   liens.py            verifie que les chemins cites existent vraiment
//   hors_ci.py          fige l'inventaire des tests exclus de la CI
//   compte_des_tests.py tient le nombre de tests annonce par le README
//
// Aucun n'est lance par quoi que ce soit. La CI lance `tools/nonuisance/*.py`
// (deux gates DURS, et ils le disent), pas ceux-la. `GATES.md`, lui, demande a
// le mainteneur de les lancer a la main -- « `python tools/gates/liens.py` ».
//
// C'est la difference entre un gate et un pense-bete. Celui qui BLOQUE tient ;
// celui qui RAPPELLE meurt, parce qu'il depend de quelqu'un qui pense a le
// lancer un jour ou il a autre chose a faire. Ces quatre-la sont ecrits, testes
// (chacun a son `--autotest`), en lecture seule -- et muets.
//
// Ce test ne juge pas ce qu'un gate verifie : il verifie qu'il est BRANCHE. Et
// il tient l'invariant dans le sens ou il compte : un gate ajoute demain et
// oublie fera rougir la suite. C'est exactement ce qui vient d'arriver a
// `compte_des_tests.py`, ecrit il y a deux tours et deja orphelin.
// -----------------------------------------------------------------------
public sealed class UnGardeQuePersonneNeLanceNeGardeRienTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string[] Gates() =>
        Directory.GetFiles(Path.Combine(Racine().FullName, "tools", "gates"), "*.py")
                 .Select(Path.GetFileName)
                 .OrderBy(n => n, StringComparer.Ordinal)
                 .ToArray()!;

    private static string Ci() =>
        File.ReadAllText(Path.Combine(Racine().FullName, ".github", "workflows", "ci.yml"));

    [Fact]
    public void Le_crible_voit_bien_les_gates_et_la_ci()
    {
        // Temoin : un dossier renomme rendrait le test suivant vert sur du vide.
        Gates().Should().Contain("liens.py").And.HaveCountGreaterThan(2);
        Ci().Should().Contain("dotnet test", "c'est bien le fichier de la CI");
    }

    [Fact]
    public void Chaque_gate_du_depot_est_lance_par_la_ci()
    {
        var ci = Ci();

        var muets = Gates().Where(nom => !ci.Contains($"tools/gates/{nom}")).ToArray();

        muets.Should().BeEmpty(
            "un gate que personne ne lance ne garde rien : il depend de quelqu'un qui pense a " +
            "le lancer. Celui qui BLOQUE tient, celui qui RAPPELLE meurt");
    }

    /// <summary>Les outils qui VERIFIENT quelque chose, hors du dossier `gates`.
    ///
    /// 19/09/2026 : cette regle ne regarde que `tools/gates/`. Les deux verificateurs de
    /// `tools/nonuisance/` sont bien dans la CI -- je croyais le contraire en ecrivant ce
    /// bloc, mon crible excluait les tirets de leurs noms de fichier -- mais rien ne tenait
    /// qu'ils y restent. Ce test le tient maintenant, dans les deux sens.
    ///
    /// Ce qui a laisse le document deriver (dix ecarts, six fichiers absents) n'est donc pas
    /// un cablage manquant : le depot est 183 commits en avance sur `origin/main` et rien
    /// n'est pousse. Un gate cable mais jamais execute garde autant qu'un gate absent.
    /// </summary>
    private static string[] Verificateurs() =>
        Directory.GetFiles(Path.Combine(Racine().FullName, "tools", "nonuisance"), "*.py")
                 .Select(Path.GetFileName)
                 .OrderBy(n => n, StringComparer.Ordinal)
                 .ToArray()!;

    [Fact]
    public void Chaque_verificateur_de_non_nuisance_est_lance_par_la_ci()
    {
        var ci = Ci();

        var muets = Verificateurs().Where(nom => !ci.Contains($"tools/nonuisance/{nom}")).ToArray();

        muets.Should().BeEmpty(
            "ces scripts refont un compte que le document affirme : sans la CI, le document " +
            "derive et personne ne l'apprend avant de le relire a la main");
    }

    [Fact]
    public void Le_crible_voit_bien_les_verificateurs_de_non_nuisance()
    {
        // Temoin : un dossier renomme rendrait le test ci-dessus vert sur du vide.
        Verificateurs().Should().Contain("count-write-sites.py").And.HaveCountGreaterThan(1);
    }

    [Fact]
    public void Un_gate_de_la_ci_existe_vraiment_sur_le_disque()
    {
        // L'autre sens : la CI ne doit pas appeler un script supprime ou renomme -- elle
        // echouerait a l'execution, en disant « fichier introuvable » au lieu de la verite.
        var cites = System.Text.RegularExpressions.Regex
            .Matches(Ci(), @"tools/gates/([A-Za-z0-9_]+\.py)")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        foreach (var nom in cites)
            File.Exists(Path.Combine(Racine().FullName, "tools", "gates", nom))
                .Should().BeTrue($"la CI lance tools/gates/{nom}");
    }
}
