using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Vu dans le rapport d'un scan reel : le service de DrDXT s'appelait « DrDXT - reglages au
// demarrage » dans la liste des services de Windows. Relu ensuite dans l'installeur : la case
// « Reappliquer mes reglages au demarrage de Windows », son groupe « Demarrage : », « Creer un raccourci »
// et le raccourci « Desinstaller DrDXT » du menu Demarrer. Tout ce que le joueur lit pendant et apres
// l'installation. Les commentaires du script, eux, ne sont pas lus par le joueur : ils ne sont pas vises.
// -----------------------------------------------------------------------
public sealed class LInstalleurParleFrancaisAvecSesAccentsTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss"));
    }

    // Les textes affiches : descriptions de case, groupes, noms de raccourci, libelles d'etat, nom de service.
    private static readonly Regex TexteAffiche = new(
        @"(?:Description|GroupDescription):\s*""(?<t>[^""]*)""|Name:\s*""\{group\}\\(?<t>[^""]*)""|Caption\s*:=\s*'(?<t>[^']*)'|DisplayName=\s*""(?<t>[^""]*)""");

    private static readonly string[] SansAccent =
        ["reglage", "demarrage", "Demarrage", "Creer", "Desinstaller", "Reappliquer", "systeme"];

    [Fact]
    public void Aucun_texte_lu_par_le_joueur_n_a_perdu_ses_accents()
    {
        // Les lignes `Type: files` effacent d'anciens fichiers : elles nomment l'ancien texte exprès.
        var affiche = string.Join("\n", Iss().Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("Type:", StringComparison.Ordinal)));
        var textes = TexteAffiche.Matches(affiche).Select(m => m.Groups["t"].Value).ToList();

        textes.Should().HaveCountGreaterThan(5, "témoin : le crible voit bien les textes de l'installeur");
        textes.Where(t => SansAccent.Any(t.Contains)).Should().BeEmpty();
    }
}
