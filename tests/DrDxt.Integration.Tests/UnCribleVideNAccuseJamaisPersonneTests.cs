using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// « AUCUN FAUTIF » est aussi ce que rend un crible qui ne lit plus rien.
//
// Ce depot tient une bonne part de ses promesses avec des cribles : on fabrique une liste
// de fautifs en lisant le livre, et on conclut `fautifs.Should().BeEmpty()`. C'est solide
// -- tant que la lecture marche. Le jour ou un dossier bouge, ou un motif ne mord plus, la
// liste est vide pour la mauvaise raison et le test reste VERT. Il annonce alors une
// garantie qu'il n'a pas regardee.
//
// Le cas a ete mesure cote Lumenia le meme jour, pas deduit : le crible du poids des
// images, prive de son parseur, laissait passer n'importe quelle image -- et son test
// passait. Ici, deux cribles etaient dans ce cas.
//
// La regle : quand un fichier de test lit le depot (enumeration de fichiers, `ReadAllText`,
// `XDocument`, `Regex.Matches`) et conclut par `BeEmpty()`, il doit aussi porter un temoin
// de lecture -- une assertion qui tombe si le crible n'a rien lu.
//
// Bruyance mesuree avant de l'ecrire : 126 fichiers concluent `BeEmpty()`. Le premier jet
// en accusait 6 ; deux accusations tombaient sur des temoins ecrits autrement
// (`BeGreaterThan(0)`, `BeGreaterThan(5)`), et deux autres sur des faux positifs nommes
// ci-dessous. Restaient 2 cribles reellement muets, corriges le meme jour.
// -----------------------------------------------------------------------
public sealed class UnCribleVideNAccuseJamaisPersonneTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Ce qui fabrique une liste a partir du depot ou du produit : ca peut se vider.</summary>
    private static readonly Regex LitLeDepot = new(
        "FichiersDuDepot|EnumerateFiles|EnumerateDirectories|GetFiles|Directory\\.|"
        + "File\\.ReadAllText|File\\.ReadAllLines|XDocument|GetResourceSet");

    private static readonly Regex ConclutVide = new(@"\.Should\(\)\s*\.\s*BeEmpty\(");

    /// <summary>Une assertion qui tombe si le crible n'a rien lu, sous ses formes employees ici.</summary>
    private static readonly Regex Temoin = new(
        @"HaveCountGreaterThan|NotBeEmpty|HaveCountGreaterOrEqualTo|Should\(\)\s*\.\s*Contain\(|"
        + @"HaveCount\(\s*[1-9]|BeGreaterThan\(\s*\d|BePositive|BeGreaterOrEqualTo\(\s*[1-9]");

    /// <summary>
    /// Les fichiers qui portent leur propre temoin sous une forme qu'aucun motif ne voit.
    ///
    /// `TheProductMayNotBuildWhatItNeverCalls` compare une mesure a une liste gelee dans les
    /// Deux sens : si la mesure ne rendait rien, le second test verrait toute la liste gelee
    /// « partie » et rougirait. Le cliquet est son propre temoin.
    ///
    /// `ManifesteFusion` n'est pas un crible : son `BeNullOrEmpty` porte sur un message
    /// d'interface, pas sur une liste fabriquee en lisant le depot.
    /// </summary>
    private static readonly string[] TemoinAutrement =
        ["TheProductMayNotBuildWhatItNeverCallsTests.cs", "ManifesteFusionTests.cs"];

    private static List<string> CriblesMuets()
    {
        var muets = new List<string>();
        foreach (var chemin in FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "tests"), "*.cs"))
        {
            var nom = Path.GetFileName(chemin);
            if (TemoinAutrement.Contains(nom, StringComparer.Ordinal)) continue;

            var texte = File.ReadAllText(chemin);
            if (ConclutVide.IsMatch(texte) && LitLeDepot.IsMatch(texte) && !Temoin.IsMatch(texte))
                muets.Add(nom);
        }
        return muets;
    }

    [Fact]
    public void Le_crible_lit_bien_les_tests_du_depot()
    {
        // Le temoin de ce crible-ci : il s'applique a lui-meme, sinon il serait le prochain.
        var fichiers = FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "tests"), "*.cs").ToList();

        fichiers.Should().HaveCountGreaterThan(200);
        fichiers.Count(f => ConclutVide.IsMatch(File.ReadAllText(f)))
            .Should().BeGreaterThan(50, "ce depot tient ses promesses avec des cribles");
    }

    [Fact]
    public void Aucun_crible_ne_conclut_AUCUN_FAUTIF_sans_prouver_qu_il_a_lu()
    {
        CriblesMuets().Should().BeEmpty(
            "une liste vide parce qu'on n'a rien lu ressemble trait pour trait a une liste " +
            "vide parce que tout va bien -- et seul le second cas est une garantie");
    }

    [Fact]
    public void Le_crible_VOIT_un_crible_muet_et_ACCEPTE_le_meme_avec_son_temoin()
    {
        const string muet = "var fautes = File.ReadAllLines(p).Where(Faux).ToList();\n"
                          + "fautes.Should().BeEmpty();";
        const string avecTemoin = "var lus = File.ReadAllLines(p);\n"
                                + "lus.Should().NotBeEmpty();\n"
                                + "lus.Where(Faux).Should().BeEmpty();";

        (LitLeDepot.IsMatch(muet) && ConclutVide.IsMatch(muet) && !Temoin.IsMatch(muet))
            .Should().BeTrue("un crible sans temoin doit ressortir");
        Temoin.IsMatch(avecTemoin).Should().BeTrue("et le meme, temoin pose, doit en sortir");
    }

    [Fact]
    public void Un_test_ordinaire_qui_n_a_rien_d_un_crible_n_est_pas_accuse()
    {
        // Contrepoids : conclure `BeEmpty()` sur un resultat de fonction n'a rien a voir.
        const string ordinaire = "var r = Calculer(3);\nr.Erreurs.Should().BeEmpty();";

        LitLeDepot.IsMatch(ordinaire).Should().BeFalse();
    }
}
