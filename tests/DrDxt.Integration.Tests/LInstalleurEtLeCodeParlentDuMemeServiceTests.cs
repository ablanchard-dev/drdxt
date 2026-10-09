using System.Text.RegularExpressions;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. L'installateur CREE le service de mesure, le helper le CHERCHE.
// Deux litteraux, dans deux langages, tenus alignes a la main :
//
//   installer/DrDXT.iss                       #define PmService "PresentMonService"
//   ElevatedHelper/Win32ServiceActions.cs     PresentMonServiceName = "PresentMonService"
//
// Le .iss dit meme, au-dessus des arguments : « Les arguments viennent de
// PresentMonNaming (DrDxt.Core), et ils sont VIDES ». La phrase decrit un lien
// qui n'existe pas -- rien ne les relie, et `PresentMonNaming` peut changer sans
// que l'installateur bouge.
//
// Ce que couterait une divergence : l'installateur enregistre un service, le
// helper en cherche un autre, et **la mesure ne demarre pas**. C'est la fonction
// centrale du produit payant -- « Mesure tes perfs en jeu (FPS, 1% lows) via
// PresentMon », promis dans le README du testeur. La desinstallation, elle,
// laisserait derriere elle le service qu'elle vient de creer.
//
// Rien ne leverait : les deux moities marchent, elles ne parlent simplement pas
// du meme service. Mesure du jour : elles sont d'accord, et personne ne le
// garantissait.
//
// Le test du 18/09 sur le README du testeur ne couvre pas ce cas : il verifie
// que le .iss est coherent avec lui-meme (`{#PmService}` arrete puis supprime),
// pas qu'il designe le service que le code connait.
// -----------------------------------------------------------------------
public sealed class LInstalleurEtLeCodeParlentDuMemeServiceTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Lire(params string[] morceaux)
        => File.ReadAllText(Path.Combine(Racine().FullName, Path.Combine(morceaux)));

    /// <summary>La valeur d'un `#define` du script d'installation.</summary>
    private static string? Define(string nom)
    {
        var m = Regex.Match(Lire("installer", "DrDXT.iss"), $@"^#define\s+{nom}\s+""([^""]*)""", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>La valeur d'une `const string` du code, par son nom.</summary>
    private static string? Constante(string fichier, string nom)
    {
        var m = Regex.Match(Lire(fichier.Split('/')), $@"\b{nom}\s*=\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void Le_crible_lit_bien_les_deux_cotes()
    {
        // Temoin de contenu : sans lui, un `#define` renomme rendrait tout vert sur du vide.
        Define("PmService").Should().NotBeNull();
        Define("AppExe").Should().NotBeNull();
        Constante("src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs", "PresentMonServiceName")
            .Should().NotBeNull();
    }

    [Fact]
    public void Les_extracteurs_lisent_une_valeur_et_pas_n_importe_quoi()
    {
        // Temoin inverse : deux extracteurs qui rendent toujours null seraient verts.
        Define("PmServiceArgs").Should().NotBeNull("un define vide reste un define");
        Define("CeDefineNExistePas").Should().BeNull();
        Constante("src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs", "CetteConstanteNExistePas")
            .Should().BeNull();
    }

    [Fact]
    public void Le_service_cree_par_l_installateur_est_celui_que_le_helper_cherche()
    {
        Define("PmService").Should().Be(
            Constante("src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs", "PresentMonServiceName"),
            "sinon l'installateur enregistre un service et le helper en cherche un autre : " +
            "la mesure ne demarre pas, et la desinstallation laisse derriere elle celui " +
            "qu'elle vient de creer");
    }

    [Fact]
    public void Le_binaire_du_service_est_celui_que_le_helper_attend()
    {
        // Le .iss ne le `#define` pas, il l'ecrit dans la commande sc.exe : on lit la commande.
        Lire("installer", "DrDXT.iss").Should().Contain(
            Constante("src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs", "PresentMonBinaryFileName")!,
            "le service pointerait sur un executable que le produit ne livre pas");
    }

    [Fact]
    public void Les_arguments_du_service_sont_bien_ceux_du_code()
    {
        // Ici on EXERCE la constante plutot que de la relire : c'est elle qui decide
        // en production, et le .iss affirme deja qu'elle est vide.
        Define("PmServiceArgs").Should().Be(PresentMonNaming.ServiceArguments,
            "le .iss annonce que ces arguments viennent de PresentMonNaming : rien ne les reliait");
    }

    [Fact]
    public void L_executable_lance_par_l_installateur_est_un_projet_de_la_solution()
    {
        // Renommer le projet casserait le raccourci du Bureau et l'icone, en silence.
        var exe = Define("AppExe")!;
        var projet = Path.Combine(Racine().FullName, "src",
            Path.GetFileNameWithoutExtension(exe), $"{Path.GetFileNameWithoutExtension(exe)}.csproj");

        File.Exists(projet).Should().BeTrue(
            $"l'installateur lance {exe}, mais aucun projet de ce nom ne le produit");
    }
}
