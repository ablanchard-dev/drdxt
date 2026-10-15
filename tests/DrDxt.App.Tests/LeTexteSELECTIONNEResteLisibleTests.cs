using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// « IrisViolet ne sert que dans des degrades decoratifs » — sauf a un endroit.
//
// `ContrasteDuChromeLiquideTests` connait deja la couleur qui echoue le seuil : IrisViolet
// donne 4,33:1 sur ObsidienneRelevee. Son commentaire ajoute, en toute bonne foi, qu'elle
// « ne sert aujourd'hui que dans des degrades decoratifs », et son crible ne regarde que les
// pinceaux employes en `Foreground`.
//
// Mesure du jour : IrisViolet est aussi le `SelectionBrush` des champs de saisie. Ce n'est
// pas une encre — c'est un FOND, pose derriere du texte pendant qu'on le selectionne. Un
// crible qui ne cherche que des `Foreground` ne peut pas le voir, par construction.
//
// Ce qui est mesure, et ce qui en depend :
//
//     texte non selectionne, sur le champ                 18,29:1
//     texte SELECTIONNE, opacite 0,45 (le reglage actuel)  10,13:1   ← lisible
//     le meme, si l'opacite passait a 1,0                   3,76:1   ← sous le seuil AA
//
// Autrement dit : la lisibilite du texte selectionne ne tient pas au choix des couleurs,
// elle tient a une seule valeur, `SelectionOpacity="0.45"`. « Rendre la selection plus
// visible » est la modification la plus naturelle du monde, elle se fait en un caractere, et
// elle rendrait illisible le texte qu'on vient de selectionner.
//
// Ou ca compte : le champ de saisie de ce produit sert a coller une clef de licence. Qu'on
// ne puisse plus la relire au moment ou on la selectionne pour la corriger est exactement le
// genre de defaut qu'on met dix minutes a comprendre et qu'on n'ecrit jamais au support.
//
// Ce fichier lit les quatre valeurs dans le XAML livre — fond, encre, pinceau de selection,
// opacite — compose le fond reel de la selection, et exige que le texte s'y lise.
// -----------------------------------------------------------------------
public sealed class LeTexteSELECTIONNEResteLisibleTests
{
    private const double AaTexteNormal = 4.5;

    private static string AppXaml()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml"));
    }

    /// <summary>Le bloc de style d'un controle, seul — pas le fichier entier.</summary>
    private static string Style(string cible)
    {
        var xaml = AppXaml();
        var debut = xaml.IndexOf($"<Style TargetType=\"{cible}\">", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, $"le style de {cible} doit exister");
        var fin = xaml.IndexOf("</Style>", debut, StringComparison.Ordinal);
        return xaml[debut..fin];
    }

    private static string Reglage(string bloc, string propriete)
    {
        var m = Regex.Match(bloc, $"Property=\"{propriete}\"\\s+Value=\"([^\"]+)\"");
        m.Success.Should().BeTrue($"{propriete} doit etre pose dans ce style");
        return m.Groups[1].Value;
    }

    /// <summary>Resout `{StaticResource XBrush}` jusqu'a la couleur hexadecimale.</summary>
    private static (int R, int G, int B) Couleur(string valeur)
    {
        var xaml = AppXaml();
        var brosse = Regex.Match(valeur, @"\{StaticResource (\w+)\}");
        brosse.Success.Should().BeTrue($"{valeur} doit renvoyer a une ressource");

        var versCouleur = Regex.Match(
            xaml, $"<SolidColorBrush x:Key=\"{brosse.Groups[1].Value}\" Color=\"\\{{StaticResource (\\w+)\\}}\"");
        versCouleur.Success.Should().BeTrue($"{brosse.Groups[1].Value} doit etre un SolidColorBrush");

        var hexa = Regex.Match(xaml, $"<Color x:Key=\"{versCouleur.Groups[1].Value}\">#([0-9A-Fa-f]{{6,8}})</Color>");
        hexa.Success.Should().BeTrue($"{versCouleur.Groups[1].Value} doit porter une valeur");

        var h = hexa.Groups[1].Value;
        if (h.Length == 8) h = h[2..];   // le canal alpha n'entre pas dans le calcul
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..], 16));
    }

    private static double Canal(double v)
    {
        v /= 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((int R, int G, int B) c)
        => 0.2126 * Canal(c.R) + 0.7152 * Canal(c.G) + 0.0722 * Canal(c.B);

    private static double Ratio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (haut, bas) = la > lb ? (la, lb) : (lb, la);
        return (haut + 0.05) / (bas + 0.05);
    }

    /// <summary>Le fond reel de la selection : le pinceau compose sur le fond du champ.</summary>
    private static (int R, int G, int B) FondDeSelection(
        (int R, int G, int B) selection, (int R, int G, int B) fond, double opacite)
        => ((int)Math.Round(opacite * selection.R + (1 - opacite) * fond.R),
            (int)Math.Round(opacite * selection.G + (1 - opacite) * fond.G),
            (int)Math.Round(opacite * selection.B + (1 - opacite) * fond.B));

    [Fact]
    public void Le_calcul_est_lui_meme_verifie()
    {
        // Deux reperes connus, sinon ce fichier n'est qu'une opinion chiffree.
        Math.Round(Ratio((0, 0, 0), (255, 255, 255)), 2).Should().Be(21.0);
        Math.Round(Ratio((119, 119, 119), (255, 255, 255)), 2).Should().Be(4.48);

        // Et la composition : un fond a moitie transparent doit tomber entre les deux.
        var moitie = FondDeSelection((255, 255, 255), (0, 0, 0), 0.5);
        moitie.R.Should().BeInRange(120, 135);
    }

    [Fact]
    public void Le_texte_selectionne_se_lit_sur_le_fond_de_selection()
    {
        var champ = Style("TextBox");

        var encre = Couleur(Reglage(champ, "Foreground"));
        var fond = Couleur(Reglage(champ, "Background"));
        var selection = Couleur(Reglage(champ, "SelectionBrush"));
        var opacite = double.Parse(Reglage(champ, "SelectionOpacity"),
                                   System.Globalization.CultureInfo.InvariantCulture);

        var reel = FondDeSelection(selection, fond, opacite);
        var ratio = Ratio(encre, reel);

        ratio.Should().BeGreaterThanOrEqualTo(AaTexteNormal,
            $"le texte selectionne doit rester lisible : encre {encre} sur le fond de "
            + $"selection compose {reel} (opacite {opacite}) donne {ratio:0.00}:1. Ce champ "
            + "sert a coller une clef de licence");
    }

    [Fact]
    public void Le_texte_NON_selectionne_se_lit_aussi()
    {
        // Contrepoids : eclaircir le fond de selection jusqu'a satisfaire le test precedent
        // ne doit pas se payer sur le cas ordinaire, qui est 99 % du temps passe dans ce champ.
        var champ = Style("TextBox");

        Ratio(Couleur(Reglage(champ, "Foreground")), Couleur(Reglage(champ, "Background")))
            .Should().BeGreaterThanOrEqualTo(AaTexteNormal);
    }

    [Fact]
    public void L_opacite_de_selection_reste_ECRITE_dans_le_style()
    {
        // Second contrepoids, et le vrai enjeu de ce fichier : toute la lisibilite tient a
        // cette valeur. Si quelqu'un retire le Setter, WPF applique son defaut — que ce
        // depot ne choisit pas — et le calcul ci-dessus ne decrit plus ce qui est affiche.
        var champ = Style("TextBox");

        champ.Should().Contain("Property=\"SelectionOpacity\"",
            "sans cette valeur, la lisibilite du texte selectionne depend d'un defaut du "
            + "framework, et ce fichier mesurerait autre chose que ce que voit l'utilisateur");
    }
}
