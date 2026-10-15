using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// `GATES.md` est le fichier dont la fonction est de BLOQUER : il dit
// ce qui n'est pas fini, ce qui n'est pas pousse, ce qui reste a trancher.
//
// Un compte comme « main local est 16 commits en avance sur origin/main » est
// honnete le jour ou il est ecrit, mais il bouge a chaque commit et personne ne
// le relit (mesure : 16 ecrit, 137 reels).
//
// Un compte qui bouge tout seul n'a pas sa place en dur dans un document : ce
// qui a sa place, c'est la COMMANDE qui le donne. Le lecteur obtient alors le
// vrai chiffre, celui du jour ou il lit.
// -----------------------------------------------------------------------
public sealed class UnDocumentDeGardeNeVieillitPasEnSilenceTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    // Les documents qui decrivent l'etat du depot, par opposition aux comptes rendus
    // dates : un compte rendu date garde le chiffre de sa date.
    private static readonly string[] DocumentsVivants = { "GATES.md", "README.md", "README.fr.md" };

    private static string Texte(string nom) => File.ReadAllText(Path.Combine(Racine().FullName, nom));

    // Ce qui bouge a chaque commit : l'avance du local sur le distant.
    //
    // Ce que le crible laisse passer, et pourquoi :
    // « l'exe du 02/09 avait 47 commits de retard sur le code (mesure le 06/09) ».
    // C'est un fait historique clos : l'ecart
    // entre un artefact date et le code a une date donnee, depuis reconstruit. Il ne
    // bougera plus, donc il ne peut pas se demoder. Un crible qui l'accuserait crierait
    // au loup, et un crible qui crie au loup finit desactive.
    private static readonly Regex CompteDeCommits =
        new(@"\b\d[\d\s ]*\s*commits?\s+(?:en\s+avance|d'avance)", RegexOptions.IgnoreCase);

    [Fact]
    public void Le_crible_reconnait_bien_un_compte_de_commits()
    {
        // Temoin inverse : sans lui, un motif casse rendrait le test suivant muet.
        CompteDeCommits.IsMatch("main local est 16 commits en avance sur origin/main").Should().BeTrue();
        CompteDeCommits.IsMatch("2 710 tests verts").Should().BeFalse("un compte de tests n'est pas un compte de commits");
        CompteDeCommits.IsMatch("l'exe du 02/09 avait 47 commits de retard sur le code")
            .Should().BeFalse("un ecart historique clos ne se demode pas : il ne doit pas etre accuse");
    }

    [Theory]
    [InlineData("GATES.md")]
    [InlineData("README.md")]
    [InlineData("README.fr.md")]
    public void Un_document_vivant_n_affirme_pas_un_compte_de_commits_en_dur(string nom)
    {
        var trouves = CompteDeCommits.Matches(Texte(nom)).Select(m => m.Value.Trim()).ToList();

        trouves.Should().BeEmpty(
            $"{nom} decrit l'etat du depot, et un compte de commits change a chaque commit. " +
            "Ecrivez la commande qui le donne (git rev-list --count origin/main..main) : le " +
            "lecteur obtient le chiffre du jour ou il lit, pas celui du jour ou on a ecrit");
    }

    [Fact]
    public void Le_document_de_garde_dit_comment_verifier_l_etat_du_depot()
    {
        // La garantie positive : retirer le chiffre ne doit pas retirer l'information.
        var gates = Texte("GATES.md");

        gates.Should().Contain("git rev-list --count origin/main..main",
            "sans la commande, le lecteur n'a plus ni le chiffre ni le moyen de l'obtenir");
    }

    [Theory]
    [InlineData("README.fr.md", @"\*\*[\d\s ]+tests")]
    [InlineData("README.md", @"\*\*[\d,]+ tests")]
    public void Le_compte_de_tests_du_readme_vient_avec_la_commande_qui_le_redonne(string fichier, string motif)
    {
        // Meme regle que ci-dessus, sur l'autre document et l'autre chiffre.
        // Le README annonce un nombre de tests. Il a porte 2 957, puis 4 205, puis 4 325,
        // et s'est trompe a chaque fois du meme cote : vers le bas, parce qu'on ajoute des
        // tests. C'est une dette inevitable — mais elle ne coute rien tant que le lecteur
        // peut obtenir le vrai chiffre, celui du jour ou il lit.
        //
        // La commande n'est pas decorative. `dotnet test` separe ses libelles de leurs
        // deux-points par une espace INSECABLE : un motif qui attend une espace ordinaire
        // rend zero sans rien dire. Une commande approximative serait pire que pas de
        // commande, parce qu'elle a l'air de marcher.
        var readme = Texte(fichier);

        readme.Should().MatchRegex(motif,
            "temoin : le README annonce bien un compte de tests");
        readme.Should().Contain("dotnet test DrDXT.sln",
            "sans la commande, le lecteur n'a qu'un chiffre date et aucun moyen de le refaire");
        readme.Should().Contain("awk",
            "la commande doit SOMMER les 18 projets : `dotnet test` n'affiche aucun total global, " +
            "et un lecteur qui lit une seule ligne croit le depot treize fois plus petit");
    }

    // -------------------------------------------------------------------
    // Meme regle sur les deux autres comptes ecrits a la main du
    // README : le nombre d'invariants, et la liste des projets.
    //
    // Qu'ils disent vrai aujourd'hui ne suffit pas : le compte des commits etait
    // juste aussi, avant de deriver de 16 a 137 sans que personne ne le relise. Un
    // compte juste que rien ne tient n'est pas garanti, il est seulement encore jeune.
    // -------------------------------------------------------------------

    private static readonly Dictionary<string, int> NombresEnLettres = new()
    {
        ["trois"] = 3, ["quatre"] = 4, ["cinq"] = 5, ["six"] = 6, ["sept"] = 7, ["huit"] = 8,
        ["three"] = 3, ["four"] = 4, ["five"] = 5, ["seven"] = 7, ["eight"] = 8,
    };

    /// <summary>Le corps d'une section `## Titre`, jusqu'au titre suivant.</summary>
    private static string Section(string texte, string debutDuTitre)
    {
        var lignes = texte.Replace("\r\n", "\n").Split('\n');
        var i = Array.FindIndex(lignes, l => l.StartsWith("## ") && l.Contains(debutDuTitre));
        i.Should().BeGreaterThanOrEqualTo(0, $"la section « {debutDuTitre} » a disparu du README");

        var suivante = Array.FindIndex(lignes, i + 1, l => l.StartsWith("## "));
        if (suivante < 0) suivante = lignes.Length;
        return string.Join('\n', lignes[(i + 1)..suivante]) + "\n" + lignes[i];
    }

    [Theory]
    [InlineData("README.fr.md", @"##\s+Les\s+(\w+)\s+invariants")]
    [InlineData("README.md", @"##\s+The\s+(\w+)\s+invariants")]
    public void Le_nombre_d_invariants_annonce_est_celui_qui_est_liste(string fichier, string motif)
    {
        var readme = Texte(fichier);
        var section = Section(readme, "invariants");

        var titre = Regex.Match(section, motif);
        titre.Success.Should().BeTrue("le titre annonce un nombre en toutes lettres");
        NombresEnLettres.Should().ContainKey(titre.Groups[1].Value,
            "le titre annonce un nombre que le test sait lire");

        // Les puces de premier niveau uniquement : un invariant, c'est `- **Nom.**`.
        var puces = Regex.Matches(section, @"^- \*\*", RegexOptions.Multiline).Count;

        puces.Should().Be(NombresEnLettres[titre.Groups[1].Value],
            $"le README annonce « les {titre.Groups[1].Value} invariants » et en liste {puces}. " +
            "La moitie du code existe pour les tenir : un invariant ajoute sans que le titre " +
            "bouge, c'est une promesse qui compte faux sur ce que le produit garantit");
    }

    [Theory]
    [InlineData("README.fr.md", "Carte du code")]
    [InlineData("README.md", "Code map")]
    public void Chaque_projet_nomme_par_la_carte_du_code_existe_vraiment(string fichier, string titre)
    {
        var carte = Section(Texte(fichier), titre);

        // `DrDxt.Reconciler*` est un joker assume : plusieurs projets, un seul role.
        var nommes = Regex.Matches(carte, @"`(DrDxt\.[A-Za-z]+)\*?`")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        nommes.Should().HaveCountGreaterThan(5, "temoin : la carte nomme bien des projets");

        var src = new DirectoryInfo(Path.Combine(Racine().FullName, "src"));
        var reels = src.GetDirectories().Select(d => d.Name).ToList();

        var absents = nommes.Where(n => !reels.Any(r => r == n || r.StartsWith(n + "."))).ToList();

        absents.Should().BeEmpty(
            "la carte du code est ce qu'un lecteur ouvre pour savoir ou vit quoi ; un projet " +
            "renomme ou fusionne la rend fausse en silence, et elle porte aussi le modele de " +
            $"securite (« le seul processus qui ecrit »). Introuvables sous src/ : {string.Join(", ", absents)}");
    }

    [Fact]
    public void Les_documents_vivants_existent_bien()
    {
        // Temoin : un fichier renomme rendrait tous les tests ci-dessus verts sur du vide.
        foreach (var nom in DocumentsVivants)
            File.Exists(Path.Combine(Racine().FullName, nom)).Should().BeTrue(nom);
    }
}
