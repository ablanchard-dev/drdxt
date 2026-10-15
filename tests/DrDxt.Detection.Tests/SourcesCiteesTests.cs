using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// -----------------------------------------------------------------------
// Les URL que le produit MONTRE au client sont sa preuve : chaque constat
// affiche la source qui l'autorise. Une source morte, ou qui redirige vers
// autre chose, c'est une affirmation que le client ne peut plus verifier.
//
// Ce test est HORS reseau, exprès : il doit dire la meme chose dans un train
// que dans la CI. Il pin ce qu'on peut savoir sans appeler personne :
//   - aucune source en http:// clair (le lien serait degrade ou bloque) ;
//   - aucune URL de source qui ne soit pas absolue ;
//   - la liste EXACTE des domaines cites, pour qu'un ajout soit un geste
//     conscient et pas un copier-coller.
// La verification VIVANTE (est-ce que la page repond encore ?) est un autre
// geste : `tools/liens/verifie.ps1`, lance a la main, dont le resultat est
// consigne dans GATES.md avec sa date.
// -----------------------------------------------------------------------
public class SourcesCiteesTests
{
    private static readonly string[] DomainesAutorises =
    {
        "learn.microsoft.com",      // documentation Windows
        "www.nvidia.com",
        "gfwsl.geforce.com",        // La source publique des versions de pilote NVIDIA, interrogée en ligne
        "www.amd.com",              // La page de téléchargement d'AMD, lue en ligne pour la dernière version
        "www.intel.com",
        "intel.github.io",
        "arxiv.org",
        "www.noobfeed.com",
        "presentmon.com",
        "github.com",
        "ablanchard-dev.github.io", // Le site GitHub Pages du projet, racine des manifestes de mise à jour signés
        "www.techpowerup.com",      // Mesure publique des limites de puissance (i9-14900K, 35 a 253 W, jeux 720p a 4K)
        "hwbusters.com",            // Mesure publique du parcage X3D (7950X3D, 1080p), graphique relu
        "www.msi.com",              // La page d'assistance d'une carte MSI (manuel), ouverte sur clic ; relevée dans un vrai navigateur
    };

    private static DirectoryInfo RacineDuDepot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln")))
            d = d.Parent;
        d.Should().NotBeNull("les tests doivent tourner dans l'arborescence du depot");
        return d!;
    }

    private static List<string> UrlsDuProduit()
    {
        var src = Path.Combine(RacineDuDepot().FullName, "src");
        var motif = new Regex(@"""(https?://[^""\s]+)""", RegexOptions.Compiled);
        var urls = new List<string>();
        foreach (var fichier in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                fichier.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            foreach (Match m in motif.Matches(File.ReadAllText(fichier)))
                urls.Add(m.Groups[1].Value);
        }
        return urls;
    }

    [Fact]
    public void Le_produit_cite_bien_des_sources()
    {
        // Sans ce garde-fou, les trois tests suivants passeraient sur une liste vide.
        UrlsDuProduit().Should().HaveCountGreaterThan(4);
    }

    [Fact]
    public void Aucune_source_n_est_en_http_clair()
    {
        UrlsDuProduit().Where(u => u.StartsWith("http://")).Should().BeEmpty();
    }

    [Fact]
    public void Chaque_source_est_une_URL_absolue_et_bien_formee()
    {
        foreach (var u in UrlsDuProduit())
            Uri.TryCreate(u, UriKind.Absolute, out _).Should().BeTrue(u);
    }

    [Fact]
    public void Aucun_domaine_n_apparait_sans_avoir_ete_decide()
    {
        var inconnus = UrlsDuProduit()
            .Select(u => new Uri(u).Host)
            .Distinct()
            .Where(h => !DomainesAutorises.Contains(h))
            .ToList();

        inconnus.Should().BeEmpty(
            "un domaine cite est une dependance de confiance : il s'ajoute a la liste sciemment");
    }
}
