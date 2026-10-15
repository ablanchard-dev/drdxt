using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Chaque bibliothèque livrée a son avis de licence. Un paquet ajouté à un projet du produit sans
/// entrer dans THIRD-PARTY-NOTICES.txt partirait chez les joueurs sans sa licence ; l'installeur affiche la GPL.
/// </summary>
public sealed class AvisDesComposantsTiersTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        return d.FullName;
    }

    [Fact]
    public void Chaque_paquet_du_produit_est_nomme_dans_l_avis()
    {
        var avis = File.ReadAllText(Path.Combine(Racine(), "THIRD-PARTY-NOTICES.txt"));
        var paquets = FichiersDuDepot.Sous(Path.Combine(Racine(), "src"), "*.csproj")
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), "PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .Distinct().ToList();

        paquets.Should().NotBeEmpty("témoin : les projets du produit sont lus");
        paquets.Where(p => !avis.Contains(p, StringComparison.Ordinal)).Should().BeEmpty(
            "un paquet livré sans son avis de licence ne part pas");
    }

    [Fact]
    public void L_installeur_affiche_la_licence_et_livre_l_avis()
    {
        File.ReadAllText(Path.Combine(Racine(), "installer", "DrDXT.iss")).Should().Contain("LicenseFile=..\\LICENSE");
        File.ReadAllText(Path.Combine(Racine(), "installer", "build-installer.ps1")).Should().Contain("THIRD-PARTY-NOTICES.txt");
    }
}
