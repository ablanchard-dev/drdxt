using FluentAssertions;

namespace DrDxt.Integration.Tests;

// En Pascal (section [Code] de l'iss), un commentaire { ... } se ferme au PREMIER `}`. Un commentaire qui cite une
// constante Inno ({app}, {sys}...) se ferme donc au milieu de la phrase, et la suite est lue comme du code : ISCC
// refuse alors de compiler (« Unknown identifier »), et aucun test ne compile l'installeur.
public sealed class LeCodeDeLInstalleurCompileTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss"));
    }

    /// <summary>Les lignes (numérotées à partir de 1) dont un commentaire { ... } s'ouvre une seconde fois avant de se
    /// fermer, dans la section [Code]. Les chaînes '...' et les commentaires // ou (* *) sont ignorés.</summary>
    internal static IReadOnlyList<int> CommentairesCoupes(string iss)
    {
        var lignes = iss.Replace("\r\n", "\n").Split('\n');
        var debut = Array.FindIndex(lignes, l => l.Trim().Equals("[Code]", StringComparison.OrdinalIgnoreCase));
        var coupes = new List<int>();
        if (debut < 0) return coupes;
        var dansAccolade = false;
        var dansParenthese = false;
        for (var n = debut + 1; n < lignes.Length; n++)
        {
            var l = lignes[n];
            var dansChaine = false;
            for (var i = 0; i < l.Length; i++)
            {
                var c = l[i];
                if (dansAccolade)
                {
                    if (c == '{') coupes.Add(n + 1);
                    else if (c == '}') dansAccolade = false;
                }
                else if (dansParenthese)
                {
                    if (c == '*' && i + 1 < l.Length && l[i + 1] == ')') { dansParenthese = false; i++; }
                }
                else if (dansChaine)
                {
                    if (c == '\'') dansChaine = false;
                }
                else if (c == '\'') dansChaine = true;
                else if (c == '/' && i + 1 < l.Length && l[i + 1] == '/') break;
                else if (c == '(' && i + 1 < l.Length && l[i + 1] == '*') { dansParenthese = true; i++; }
                else if (c == '{') dansAccolade = true;
            }
        }
        return coupes.Distinct().ToList();
    }

    [Fact]
    public void Aucun_commentaire_du_code_ne_cite_une_constante_entre_accolades()
    {
        var iss = Iss();
        // Témoin sur le vrai fichier : une section [Code] lue, et des commentaires { } dedans.
        iss.Should().Contain("[Code]").And.Contain("{ ");
        CommentairesCoupes(iss).Should().BeEmpty(
            "un `{` dans un commentaire { ... } (une constante Inno comme {app}) le ferme au `}` suivant, et ISCC " +
            "lit la suite comme du code : l'installeur ne se construit plus");
    }

    [Fact]
    public void Le_crible_voit_un_commentaire_coupe_et_ignore_les_chaines()
    {
        // Témoins : sans eux, un crible qui ne lit rien serait vert.
        CommentairesCoupes("[Code]\n  { les .exe de {app} ouverts }\n").Should().Equal(2);
        CommentairesCoupes("[Code]\n  Exec(ExpandConstant('{sys}\\sc.exe'), '', '', 0, 0, R); { normal }\n").Should().BeEmpty();
        CommentairesCoupes("[Code]\n  (* le dossier {app} *) // et {app} ici\n").Should().BeEmpty();
    }
}
