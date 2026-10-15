using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests.Updates;

// -----------------------------------------------------------------------
// Aucune doc du chemin de mise a jour ne promet un epinglage du serveur : il n'existe pas.
// Le client HTTP de la mise a jour est nu, et l'epinglage SPKI du serveur se posera avec son
// deploiement (ses empreintes n'existent pas encore).
//
// Pourquoi ca compte plus qu'une faute de style. Quelqu'un qui audite ce transport (dans six
// mois, un client, un acheteur) lit le fichier qui porte le mot « PINNED » et coche la case. Une documentation fausse ne se contente pas d'etre fausse : elle EMPECHE
// de trouver le trou, parce qu'elle repond a la question qu'on venait poser. Et le canal
// concerne installe des executables sur une machine ou le produit tourne en administrateur.
//
// Ce qui protege reellement ce canal aujourd'hui, et qui est teste ailleurs :
//   * l'URL doit etre une adresse absolue en HTTPS, sinon rien n'est monte ;
//   * sans clef de verification de PRODUCTION, rien n'est monte non plus ;
//   * le manifeste est signe en Ed25519 et le binaire verifie par son sha256.
// L'epinglage viendra quand le serveur aura des empreintes. D'ici la, on le DIT.
//
// Ce crible refuse qu'une doc du chemin de mise a jour reclame un epinglage tant qu'aucun
// n'existe pour ce serveur.
// -----------------------------------------------------------------------
public sealed class AucuneDocNePrometUnEpinglageQuiNExistePasTests
{
    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> FichiersDuCheminDeMiseAJour()
    {
        var racine = Racine();
        foreach (var dossier in new[]
                 {
                     Path.Combine(racine, "src", "DrDxt.Infrastructure", "Updates"),
                     Path.Combine(racine, "src", "DrDxt.App", "Updates"),
                 })
        {
            if (!Directory.Exists(dossier)) continue;
            foreach (var f in Directory.EnumerateFiles(dossier, "*.cs", SearchOption.AllDirectories))
                yield return f;
        }
    }

    /// <summary>Les fabriques de clients epingles que le depot propose vraiment.</summary>
    private static string[] FabriquesEpinglees()
    {
        // La seule fabrique (CreateForKeygen) est partie avec la licence. On cherche
        // desormais dans tout src/, pour qu'une fabrique ajoutee ailleurs demain soit vue.
        return Directory.EnumerateFiles(Path.Combine(Racine(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .Where(t => t.Contains("class PinnedHttpClientFactory"))
            .SelectMany(t => Regex.Matches(t, @"public static [^\s]+ (Create\w+)\s*\(").Select(m => m.Groups[1].Value))
            .ToArray();
    }

    /// <summary>Les commentaires groupes en blocs : des lignes `//` consecutives forment
    /// Un texte. C'est l'unite que quelqu'un lit, donc l'unite qu'on juge.</summary>
    private static IEnumerable<string> BlocsDeCommentaires(string fichier)
    {
        var bloc = new List<string>();
        foreach (var ligne in File.ReadAllLines(fichier))
        {
            var texte = ligne.Trim();
            if (texte.StartsWith("//"))
            {
                bloc.Add(texte);
                continue;
            }

            if (bloc.Count > 0)
            {
                yield return string.Join(" ", bloc);
                bloc.Clear();
            }
        }

        if (bloc.Count > 0) yield return string.Join(" ", bloc);
    }

    [Fact]
    public void Le_crible_lit_bien_le_chemin_de_mise_a_jour()
    {
        // Temoin : sans lui, un dossier renomme rendrait tout vert sur du vide.
        var fichiers = FichiersDuCheminDeMiseAJour().ToList();

        fichiers.Should().NotBeEmpty();
        fichiers.Should().Contain(f => Path.GetFileName(f) == "UpdateSeams.cs");
    }

    [Fact]
    public void Aucune_fabrique_epinglee_n_existe_aujourd_hui()
    {
        // Le fait sur lequel tout le reste repose, verifie plutot que suppose. Le jour ou
        // une fabrique pour le serveur de mise a jour apparait, ce test rougit et le
        // paragraphe ci-dessus doit etre reecrit — c'est exactement ce qu'on veut.
        FabriquesEpinglees().Should().BeEmpty();
    }

    [Fact]
    public void Aucune_doc_du_chemin_ne_reclame_un_client_epingle()
    {
        var couvertParUneFabrique = FabriquesEpinglees()
            .Any(n => n.Contains("Update", StringComparison.OrdinalIgnoreCase));

        var promesses = new List<string>();
        foreach (var fichier in FichiersDuCheminDeMiseAJour())
        {
            // Deuxieme version de ce crible. La premiere jugeait ligne par ligne, et
            // accusait encore le correctif : l'explication du defaut tient sur trois lignes,
            // et celle du milieu contient « un client epingle » sans porter, a elle seule, la
            // mention du manque. Un lecteur ne lit pas une ligne, il lit un PARAGRAPHE — le
            // crible doit juger la meme unite, sinon il interdit d'ecrire pourquoi.
            foreach (var texte in BlocsDeCommentaires(fichier))
            {

                // Un commentaire qui explique pourquoi une phrase est fausse peut CITER le mot
                // (« "PINNED" »). Un crible qui ne distingue pas une citation d'une affirmation
                // interdirait d'expliquer le defaut, c'est-a-dire la seule chose qui empeche de
                // le refaire. Un mot entre guillemets est cite, pas promis.
                var sansCitation = Regex.Replace(texte, "«[^»]*»", " ");
                sansCitation = Regex.Replace(sansCitation, "\"[^\"]*\"", " ");

                // « la forme canonique est pinned » parle d'un format FIGE, pas d'un
                // certificat : le mot a deux sens, et n'accuser que celui qui porte sur le
                // transport evite d'apprendre a ce crible a crier au loup.
                var parleDuTransport = Regex.IsMatch(sansCitation, @"client|transport|HttpClient");

                if (parleDuTransport
                    && Regex.IsMatch(sansCitation, @"\bPINNED\b|\bpinned\b|\bepingle\b")
                    && !texte.Contains("n'existent pas", StringComparison.Ordinal)
                    && !texte.Contains("pas encore", StringComparison.Ordinal))
                {
                    promesses.Add($"{Path.GetFileName(fichier)} : {texte}");
                }
            }
        }

        if (couvertParUneFabrique) return;   // le jour ou l'epinglage existe, la promesse devient vraie

        promesses.Should().BeEmpty(
            "aucune fabrique n'epingle le serveur de mise a jour : une doc qui l'affirme "
            + "repond a la question qu'un auditeur venait poser, et l'empeche de trouver le trou");
    }
}
