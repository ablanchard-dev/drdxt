using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// L'installeur emporte tout le dossier de publication :
//
//   Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
//
// Or `dotnet publish` y depose aussi les **symboles de debogage**. Mesure du jour
// sur le payload reellement construit : **13 fichiers `.pdb`, 576 Ko**, et ils ne
// contiennent pas que des numeros de ligne. Lus octet par octet :
//
//   DrDxt.ElevatedHelper.pdb   43 Ko   57 noms de fichiers .cs
//   DrDxt.App.pdb             137 Ko   97 noms de fichiers .cs
//   DrDxt.Licensing.pdb        24 Ko   22 noms de fichiers .cs
//
// et chacun porte le chemin de l'arborescence d'origine, jusqu'au nom du dossier
// personnel de son auteur (« blanc ... DrDXT ... src ... »).
//
// Ce que ca donne a qui recoit l'installeur d'un produit FERME et payant : la
// carte du code, fichier par fichier, methode par methode -- pour le helper
// eleve en particulier, c'est-a-dire le composant dont l'allowlist default-DENY
// Est le moat du produit. Et le nom d'utilisateur de la machine de developpement
// en prime.
//
// Le correctif garde les symboles la ou ils servent (la machine de build, pour
// lire une pile d'appel) et les retire de ce qui part chez le client. C'est la
// difference entre « ne pas les produire » et « ne pas les livrer ».
// -----------------------------------------------------------------------
public sealed class LInstalleurNEmporteAucunSymboleTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss"));
    }

    /// <summary>La ligne `[Files]` qui emporte le payload complet.</summary>
    private static string LigneDuPayload()
    {
        var ligne = Iss().Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Source:") && l.Contains("publish"));

        ligne.Should().NotBeNull("l'installeur doit toujours emporter le dossier de publication");
        return ligne!;
    }

    [Fact]
    public void Le_crible_lit_bien_la_ligne_qui_emporte_le_payload()
    {
        // Temoin : sans lui, une section renommee rendrait le test suivant vert sur du vide.
        var ligne = LigneDuPayload();

        ligne.Should().Contain("DestDir");
        ligne.Should().Contain("recursesubdirs", "c'est bien tout le dossier qui part, sous-dossiers compris");
    }

    [Fact]
    public void Aucun_symbole_de_debogage_ne_part_chez_le_client()
    {
        var ligne = LigneDuPayload();

        var exclusions = Regex.Match(ligne, @"Excludes:\s*""([^""]*)""");

        exclusions.Success.Should().BeTrue(
            "la ligne emporte `publish\\*` en entier, et `dotnet publish` y depose les `.pdb` : " +
            "sans `Excludes`, les symboles partent avec le produit");

        exclusions.Groups[1].Value.Should().Contain("*.pdb",
            "un `.pdb` porte la carte du code source -- noms de fichiers, methodes, lignes -- et le " +
            "chemin de l'arborescence de build. Sur un produit ferme dont le moat est l'allowlist du " +
            "helper eleve, c'est le plan du coffre livre avec le coffre");
    }

    [Fact]
    public void Les_symboles_restent_produits_pour_la_machine_de_build()
    {
        // L'autre sens, et il compte : la reponse facile aurait ete `DebugType=none`, qui
        // supprime les symboles PARTOUT. Une pile d'appel sans numero de ligne coute cher
        // le jour d'un plantage chez un testeur. On ne les produit pas moins -- on les
        // livre pas.
        //
        // Premiere version : elle cherchait la chaine dans tout le script, et elle a
        // rougi sur mon propre commentaire, qui la cite pour expliquer ce qu'on ne fait
        // pas. L'instrument s'accusait lui-meme. Les lignes de commentaire (`;`) sont
        // donc retirees d'abord, comme le fait deja le crible du service.
        var script = string.Join('\n', Iss().Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith(';')));

        script.Should().NotContain("DebugType=none",
            "les symboles doivent rester disponibles cote build ; c'est leur LIVRAISON qui est refusee");
    }
}
