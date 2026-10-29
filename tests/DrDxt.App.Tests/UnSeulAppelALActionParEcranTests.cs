using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le style `CapsuleChrome` porte sa propre definition dans App.xaml :
// « l'action principale d'UN ecran ». Au singulier. C'est le seul bouton du
// theme qui recoit un fond clair et un halo : deux capsules cote a cote, ce sont
// deux actions qui se disputent le meme regard, et plus aucune qui se detache.
//
// Mesure du jour : LicenseView en portait deux -- « Acheter » et « Activer ».
// Or `Buy` ne fait rien dans l'application : il ouvre le navigateur. C'est une
// sortie, pas l'action de l'ecran. L'action de l'ecran Licence, c'est activer.
//
// Ce fichier tient la regle au niveau du theme, pas d'un ecran : une capsule au
// plus par vue, et les deux ecrans qui ont une action en gardent bien une.
// -----------------------------------------------------------------------
public sealed class UnSeulAppelALActionParEcranTests
{
    private const string Capsule = "CapsuleChrome";

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Les vues servies a l'ecran, pas les dictionnaires de ressources.</summary>
    private static IEnumerable<string> Vues()
    {
        var app = Path.Combine(Racine().FullName, "src", "DrDxt.App");
        return Directory.EnumerateFiles(Path.Combine(app, "Views"), "*.xaml")
                        .Append(Path.Combine(app, "MainWindow.xaml"))
                        .Where(File.Exists);
    }

    private static int CapsulesDe(string fichier)
        => Regex.Matches(Path.GetFileName(fichier) == "ScanView.xaml" ? VueScan.Texte(fichier) : File.ReadAllText(fichier), $@"Style=""\{{StaticResource {Capsule}\}}""").Count;

    [Fact]
    public void Le_crible_voit_bien_des_capsules_dans_les_vues()
    {
        // Sans ce temoin, un motif casse rendrait les deux tests suivants muets.
        Vues().Sum(CapsulesDe).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Aucun_ecran_ne_propose_deux_actions_principales()
    {
        var bavards = Vues()
            .Select(v => (Nom: Path.GetFileName(v), Nombre: CapsulesDe(v)))
            .Where(v => v.Nombre > 1)
            .Select(v => $"{v.Nom} : {v.Nombre}")
            .ToList();

        bavards.Should().BeEmpty(
            $"le style {Capsule} se definit lui-meme comme l'action principale d'UN ecran. " +
            "Deux capsules cote a cote se disputent le regard et aucune ne se detache : " +
            "le second bouton doit passer en BoutonReflet");
    }

    [Theory]
    [InlineData("ScanView.xaml")]
    public void Les_ecrans_qui_agissent_gardent_leur_action_principale(string vue)
    {
        // Le garde du dessus serait content d'un theme ou plus personne n'a de
        // capsule. Ces deux ecrans-la demandent un geste : il doit se voir.
        var fichier = Vues().Single(v => Path.GetFileName(v) == vue);

        CapsulesDe(fichier).Should().Be(1);
    }
}
