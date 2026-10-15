using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Application.Tests;

// -----------------------------------------------------------------------
// Le README ouvre sur deux promesses : « il ne dit jamais un chiffre
// qu'il n'a pas mesure, et il n'applique jamais un levier qu'il ne peut pas
// defaire ». La seconde tenait a la FORME du contrat -- `ITweak` expose bien un
// `RevertAsync` -- et rien de plus.
//
// Or une signature ne defait rien. `RevertAsync` peut rendre `Result.Ok()` sans
// toucher a la machine, et la promesse serait fausse sans qu'aucun test ne
// rougisse : c'est exactement la forme de defaut que cette boucle cherche.
//
// Mesure du jour : les huit leviers qui definissent un revert le tiennent. Le
// seul dont le revert ne fait rien -- le correctif de pilote GPU -- n'ecrit rien
// non plus : il ouvre un guide, l'utilisateur agit lui-meme. Rien a defaire.
//
// Ce crible relit les sources et refuse qu'un levier ajoute demain ecrive sur la
// machine sans savoir revenir en arriere.
// -----------------------------------------------------------------------
public sealed class ChaqueLevierQuiEcritSaitDefaireTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static IEnumerable<FileInfo> Leviers()
        => new DirectoryInfo(Path.Combine(Racine().FullName, "src", "DrDxt.Application", "Tweaks"))
            .GetFiles("*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// Le corps d'une methode, compte accolade par accolade depuis sa DEFINITION.
    /// Un premier essai coupait une tranche de texte apres la premiere occurrence du
    /// nom : il tombait sur des commentaires et accusait cinq bases saines. Un
    /// resultat extreme accuse l'instrument avant le code.
    /// </summary>
    private static string? CorpsDe(string source, string methode)
    {
        var m = Regex.Match(source, $@"(?:public|protected|private)[^\n;]*\b{methode}\s*\([^)]*\)\s*(?:=>|\{{)");
        if (!m.Success) return null;

        var i = m.Index + m.Length;
        if (source.Substring(m.Index + m.Length - 2, 2) == "=>")
            return source[i..(source.IndexOf(';', i) + 1)];

        var profondeur = 1;
        var j = i;
        while (profondeur > 0 && j < source.Length)
        {
            if (source[j] == '{') profondeur++;
            else if (source[j] == '}') profondeur--;
            j++;
        }
        return source[i..j];
    }

    private sealed record Levier(string Nom, bool Ecrit, bool SaitDefaire);

    private static List<Levier> LesLeviers()
    {
        var liste = new List<Levier>();
        foreach (var fichier in Leviers())
        {
            var source = File.ReadAllText(fichier.FullName);
            var revert = CorpsDe(source, "RevertAsync");
            if (revert is null) continue;   // le levier herite du revert de sa base

            var applique = CorpsDe(source, "ApplyAsync") ?? string.Empty;
            liste.Add(new Levier(
                fichier.Name,
                // Ecrire sur la machine passe toujours par le helper privilegie.
                Ecrit: applique.Contains("ExecuteAsync"),
                SaitDefaire: revert.Contains("OriginalValueJson") || revert.Contains("snapshot")));
        }
        return liste;
    }

    [Fact]
    public void Le_crible_voit_bien_les_leviers()
    {
        // Sans ce temoin, un motif casse rendrait le test suivant vert sur le vide.
        var leviers = LesLeviers();

        leviers.Should().HaveCountGreaterThan(5);
        leviers.Should().Contain(l => l.Ecrit, "au moins un levier ecrit vraiment sur la machine");
    }

    [Fact]
    public void Le_crible_sait_lire_un_corps_de_methode()
    {
        // Temoin inverse, et memoire du piege : ce crible s'est deja trompe.
        const string faux = @"
            // RevertAsync : ce commentaire parle du snapshot mais ne fait rien.
            public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());";
        const string vrai = @"
            public async Task<Result> RevertAsync(TweakSnapshot snapshot)
            {
                var op = Decode(snapshot.OriginalValueJson);
                return await _elevated.ExecuteAsync(op);
            }";

        CorpsDe(faux, "RevertAsync").Should().NotContain("OriginalValueJson");
        CorpsDe(vrai, "RevertAsync").Should().Contain("OriginalValueJson");
    }

    [Fact]
    public void Aucun_levier_n_ecrit_sur_la_machine_sans_savoir_revenir_en_arriere()
    {
        var muets = LesLeviers()
            .Where(l => l.Ecrit && !l.SaitDefaire)
            .Select(l => l.Nom)
            .ToList();

        muets.Should().BeEmpty(
            "le README promet qu'aucun levier n'est applique sans pouvoir etre defait. " +
            "Un RevertAsync qui ignore son instantane rend cette phrase fausse : soit il " +
            "restaure l'etat d'origine, soit le levier ne doit rien ecrire");
    }
}
