using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Mesure sur le payload reellement construit (installer\publish) :
// **PresentMonService.exe y est deux fois, 3,6 Mo chacun, empreinte identique**
// (md5 5041ba385fcc56db5cd2d673e2f3288f) :
//
//   publish\PresentMonService.exe                          <- copie par build-installer.ps1 (etape 3)
//   publish\third_party\presentmon\PresentMonService.exe   <- deposee par le csproj du helper
//
// Les deux existent pour de bonnes raisons prises separement : l'installeur
// enregistre le service depuis `{app}\PresentMonService.exe` (section [Code]),
// et le csproj du helper depose une copie sous `third_party\presentmon\` pour
// que le locator la trouve **en developpement**, quand rien n'a ete copie a la
// racine.
//
// Mais le locator (Win32ServiceActions.DefaultPresentMonBinaryLocator) essaie
// la racine EN premier, et la racine est garantie par l'etape 3. Dans une
// installation reelle, la copie sous `third_party\` n'est donc **jamais lue** :
// c'est 3,6 Mo tele charges, decompresses et poses sur le disque du client que
// le code ne peut pas atteindre -- et un second exemplaire, a un chemin que
// l'installeur n'enregistre pas, du binaire de service que le helper eleve
// lance.
//
// Le correctif ne supprime rien : la copie reste produite la ou elle sert
// (le dossier de build, pour le dev). C'est sa LIVRAISON qui est refusee.
// Meme geste que pour les `.pdb` la veille, et pour la meme raison : ce qui
// sert cote build n'a pas a partir chez le client.
// -----------------------------------------------------------------------
public sealed class LeServiceNEstEmbarqueQuUneFoisTests
{
    private static string RacineDuDepot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static string Iss() => File.ReadAllText(Path.Combine(RacineDuDepot(), "installer", "DrDXT.iss"));

    private static string LigneDuPayload() =>
        Iss().Replace("\r\n", "\n").Split('\n')
            .First(l => l.TrimStart().StartsWith("Source:") && l.Contains("publish"));

    private static string Locator() =>
        File.ReadAllText(Path.Combine(RacineDuDepot(), "src", "DrDxt.ElevatedHelper", "Execution", "Win32ServiceActions.cs"));

    [Fact]
    public void Le_crible_lit_bien_les_deux_pieces()
    {
        // Temoin : sans lui, un fichier renomme rendrait les tests suivants verts sur du vide.
        LigneDuPayload().Should().Contain("recursesubdirs");
        Locator().Should().Contain("DefaultPresentMonBinaryLocator");
    }

    [Fact]
    public void La_copie_de_developpement_est_bien_celle_que_le_produit_ne_lit_jamais()
    {
        // L'ordre des candidats est la raison pour laquelle la seconde copie est morte
        // chez le client. Si un jour il s'inverse, la seconde copie redevient la seule
        // lue -- et l'exclusion ci-dessous casserait l'installation. Ce test tient cet
        // ordre, pour que l'exclusion reste justifiee au lieu d'etre heritee.
        var source = Locator();
        var racine = source.IndexOf("Path.Combine(baseDir, PresentMonBinaryFileName)", StringComparison.Ordinal);
        var vendu = source.IndexOf("Path.Combine(baseDir, \"third_party\"", StringComparison.Ordinal);

        racine.Should().BeGreaterThan(0, "le locator doit toujours essayer la racine");
        vendu.Should().BeGreaterThan(0, "le locator doit garder son repli de developpement");
        racine.Should().BeLessThan(vendu,
            "la racine est essayee en premier, et l'installeur la garantit : c'est ce qui rend " +
            "la copie sous third_party inatteignable dans une installation reelle");
    }

    [Fact]
    public void Aucune_seconde_copie_du_service_ne_part_chez_le_client()
    {
        var exclusions = Regex.Match(LigneDuPayload(), @"Excludes:\s*""([^""]*)""");

        exclusions.Success.Should().BeTrue("la ligne emporte publish\\* en entier, sous-dossiers compris");

        exclusions.Groups[1].Value.Should().Contain(@"\third_party",
            "le dossier third_party du payload ne contient qu'un second exemplaire de " +
            "PresentMonService.exe (3,6 Mo, empreinte identique a celui de la racine) que le " +
            "locator n'atteint jamais, puisqu'il essaie la racine d'abord. Le livrer, c'est " +
            "poser chez le client un binaire de service que l'installeur n'enregistre pas");
    }

    [Fact]
    public void Le_repli_de_developpement_reste_produit()
    {
        // L'autre sens : la reponse facile aurait ete de retirer le <None Include> du csproj,
        // ce qui casse le locator hors installation -- exactement le cas ou le repli sert.
        var csproj = File.ReadAllText(Path.Combine(
            RacineDuDepot(), "src", "DrDxt.ElevatedHelper", "DrDxt.ElevatedHelper.csproj"));

        // Premiere version : elle cherchait la chaine dans tout le csproj, et la mutation
        // (Link ramene a la racine) ne l'a pas fait rougir -- le chemin SOURCE du <None Include>
        // contient deja la meme sous-chaine. Le test nommait la garantie sans la prouver. C'est
        // le `Link` qui decide ou le fichier atterrit dans la sortie de build : c'est lui qu'on tient.
        csproj.Should().MatchRegex(@"Link\s*=\s*""third_party\\presentmon\\PresentMonService\.exe""",
            "le repli doit rester DEPOSE sous third_party\\presentmon\\ cote build -- c'est ce chemin " +
            "que le locator essaie en second, et c'est sa livraison qui est refusee, pas son existence");
    }
}
