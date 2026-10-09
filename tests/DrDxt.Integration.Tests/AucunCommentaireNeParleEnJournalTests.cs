using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// 28/09/2026 : huit commentaires du produit racontaient leur auteur (« j'ai d'abord cru », « j'avais gravé le
// 07/09 », « ça m'est arrivé »). Le fond était utile, la voix était celle d'un journal de session. Un
// commentaire dit pourquoi le code est ainsi, au neutre. Entre guillemets, « je n'ai pas pu lire » reste
// permis : c'est ce que le produit dit au joueur, pas l'auteur qui parle.
public sealed class AucunCommentaireNeParleEnJournalTests
{
    private static readonly Regex PremierePersonne =
        new(@"\b(j'ai|je l'ai|mon erreur|c'est moi|je n'ai|j'avais|je me suis|ma faute|moi-m[eê]me|m'ont|m'a)\b", RegexOptions.IgnoreCase);

    /// <summary>Retire ce qui est cité : la fin d'une citation ouverte à la ligne d'avant, puis chaque « … ».</summary>
    public static string SansCitations(string ligne)
    {
        var s = Regex.Replace(ligne, "^[^«]*»", "");
        return Regex.Replace(s, "«[^»]*(»|$)", "");
    }

    [Fact]
    public void Le_crible_distingue_l_auteur_de_ce_que_dit_le_produit()
    {
        PremierePersonne.IsMatch(SansCitations("// J'ai d'abord cru qu'AABB serait singulier.")).Should().BeTrue();
        PremierePersonne.IsMatch(SansCitations("// une liste vide ferait passer « je n'ai pas pu lire » pour « rien »")).Should().BeFalse();
        PremierePersonne.IsMatch(SansCitations("/// reste « je n'ai pas pu")).Should().BeFalse();
        PremierePersonne.IsMatch(SansCitations("/// vérifier » quoi qu'il arrive")).Should().BeFalse();
    }

    [Fact]
    public void Aucun_commentaire_du_produit_ne_parle_a_la_premiere_personne()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var src = Path.Combine(d!.FullName, "src");
        var fichiers = new[] { "*.cs", "*.xaml" }.SelectMany(m => FichiersDuDepot.Sous(src, m))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        fichiers.Should().HaveCountGreaterThan(100, "temoin : le crible lit le produit");

        var fautes = fichiers
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, t: l.Trim())))
            .Where(x => Regex.IsMatch(x.t, @"^(//|\*|<!--)") && PremierePersonne.IsMatch(SansCitations(x.t)))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        fautes.Should().BeEmpty();
    }
}
