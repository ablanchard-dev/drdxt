using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Le README annonce le modele de securite du produit :
//
//   « Tout ce qui ecrit passe par une op typee serialisee vers le helper eleve,
//     validee par une allowlist default-DENY, feuille-exacte. »
//
// Et la carte du code designe `DrDxt.ElevatedHelper` comme « **le seul** processus
// qui ecrit ».
//
// Les gardes existantes tiennent l'allowlist : elles verifient que les ops connues
// sont refusees par defaut et que leurs feuilles sont exactes. Elles regardent donc
// le chemin PREVU. Aucune ne dit ce qui arrive si quelqu'un ecrit **a cote** --
// un `CreateSubKey` dans l'App, un `ServiceController.Stop()` dans Detection. Ce
// code-la ne franchit aucune allowlist : il n'en rencontre pas.
//
// Un crible juste peut regarder a cote : il couvre parfaitement le chemin qu'il
// connait, alors que la promesse porte sur tout le code.
//
// Mesure du jour, sans defaut trouve : zero ecriture registre et zero action de
// service hors du helper. La discipline tenait a la main, sur cinquante projets.
//
// Bruyance mesuree avant d'ecrire : zero accusation. Le tri par fichier est ce qui
// l'obtient -- `SetValue(` seul accuserait tous les effets WPF, ou c'est
// `DependencyObject.SetValue`, et un crible qui accuse l'interface graphique de
// toucher au registre se fait desactiver la semaine suivante.
// -----------------------------------------------------------------------
public sealed class SeulLeHelperEcritSurLaMachineTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Le code livre, helper exclu : c'est lui qui a le droit d'ecrire.</summary>
    private static List<FileInfo> HorsHelper()
        => FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs").Select(p => new FileInfo(p))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains("DrDxt.ElevatedHelper"))
            .ToList();

    // Deux conditions par accusation : le fichier doit PARLER de la surface systeme,
    // et appeler une ecriture. L'une sans l'autre ne prouve rien.
    private static readonly Regex ParleRegistre = new(@"Microsoft\.Win32|RegistryKey");
    private static readonly Regex EcritRegistre = new(@"\.SetValue\(|\.CreateSubKey\(|\.DeleteValue\(|\.DeleteSubKey");
    private static readonly Regex ParleService = new(@"ServiceController");
    private static readonly Regex AgitSurService = new(@"\.Start\(\)|\.Stop\(\)|\.WaitForStatus");

    private static List<string> Coupables(Regex parle, Regex agit)
        => HorsHelper()
            .Select(f => (f.Name, Texte: File.ReadAllText(f.FullName)))
            .Where(x => parle.IsMatch(x.Texte) && agit.IsMatch(x.Texte))
            .Select(x => x.Name)
            .ToList();

    [Fact]
    public void Le_crible_lit_bien_le_code_livre_hors_helper()
    {
        // Temoin de contenu : sans lui, un chemin change rendrait tout vert sur zero fichier.
        var fichiers = HorsHelper();

        fichiers.Should().HaveCountGreaterThan(200);
        fichiers.Should().Contain(f => f.Name == "App.xaml.cs");
        fichiers.Should().NotContain(f => f.FullName.Contains("DrDxt.ElevatedHelper"),
            "le helper est justement celui qui a le droit d'ecrire");
    }

    [Fact]
    public void Le_crible_exige_les_DEUX_conditions()
    {
        // Temoin inverse, sur le faux positif exact qu'il evite. Dans les effets WPF,
        // `SetValue` est `DependencyObject.SetValue` : ecrire une propriete de
        // dependance n'est pas toucher au registre.
        EcritRegistre.IsMatch("btn.SetValue(OpacityProperty, 0.5);").Should().BeTrue(
            "le motif d'ecriture seul mord sur du WPF");
        ParleRegistre.IsMatch("btn.SetValue(OpacityProperty, 0.5);").Should().BeFalse(
            "mais le fichier ne parle pas du registre, donc il n'est pas accuse");

        ParleRegistre.IsMatch("using Microsoft.Win32;").Should().BeTrue();
        EcritRegistre.IsMatch(@"cle.CreateSubKey(""SOFTWARE\\DrDXT"");").Should().BeTrue();
        EcritRegistre.IsMatch(@"cle.OpenSubKey(""SOFTWARE\\DrDXT"");").Should().BeFalse(
            "lire n'est pas ecrire : c'est tout l'objet du produit");
    }

    [Fact]
    public void Rien_hors_du_helper_n_ecrit_dans_le_registre()
    {
        Coupables(ParleRegistre, EcritRegistre).Should().BeEmpty(
            "le README promet que tout ce qui ecrit passe par une op typee validee par " +
            "une allowlist default-DENY. Une ecriture posee ailleurs ne franchit aucune " +
            "allowlist : elle n'en rencontre pas, et le revert n'en sait rien");
    }

    [Fact]
    public void Rien_hors_du_helper_ne_demarre_ni_n_arrete_un_service()
    {
        Coupables(ParleService, AgitSurService).Should().BeEmpty(
            "arreter un service depuis l'App contournerait l'op typee, donc la capture de " +
            "l'etat d'origine : le produit promet de pouvoir tout defaire, et il ne saurait " +
            "pas qu'il y a quelque chose a defaire");
    }
}
