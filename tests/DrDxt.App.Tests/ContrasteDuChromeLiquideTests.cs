using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le contraste du texte, lu sur les ressources reellement chargees par App.xaml
// -- pas sur une relecture du fichier. Une couleur qui ne se lit pas est un
// defaut silencieux : rien ne leve, rien ne rougit, et l'utilisateur plisse
// les yeux devant un chiffre qu'on lui vend comme une mesure.
//
// Seuil : WCAG 2.1 AA, 4,5:1 pour le texte courant. Chaque pinceau teste ici
// est employe comme Foreground dans les XAML -- ce n'est plus une verification
// humaine mais un crible, plus bas : c'est donc bien le seuil du TEXTE qui
// s'applique, pas les 3:1 des elements graphiques.
// -----------------------------------------------------------------------
[Collection(CollectionWpf.Nom)]
public sealed class ContrasteDuChromeLiquideTests
{
    private const double AaTexteNormal = 4.5;

    // Les trois fonds sur lesquels le chrome pose du texte.
    private static readonly string[] Fonds =
    {
        "ObsidienneBrush", "ObsidienneHauteBrush", "ObsidienneReleveeBrush"
    };

    // Ce que les vues passent en Foreground sur le chrome sombre. Le crible plus
    // bas refuse que cette liste prenne du retard sur le XAML.
    private static readonly string[] Encres =
    {
        "TexteBrush", "TexteSecondaireBrush", "TexteDiscretBrush",
        "IrisCyanBrush", "EtatCorrigerBrush", "EtatRegarderBrush",
        "EtatRienBrush", "EtatNonLuBrush", "ChromeTexteBrush"
    };

    // Les encres posees sur un fond de COULEUR, pas sur le chrome sombre.
    private static readonly string[] EncresSurAccent = { "SurChromeBrush" };

    // Les fonds clairs ou colores sur lesquels ces encres sont posees, releves dans
    // App.xaml : le corps de la capsule chrome et de l'onglet actif du selecteur
    // (ChromeCapsuleBrush, un degrade -- chacun de ses arrets est teste), et le rouge
    // du bouton fermer au survol.
    private static readonly string[] FondsAccent = { "ChromeCapsuleBrush", "EtatCorrigerBrush" };

    /// <summary>Les couleurs d'un fond : une pour un aplat, une par arret pour un degrade.</summary>
    private static IEnumerable<Color> CouleursDuFond(object ressource) => ressource switch
    {
        SolidColorBrush aplat => new[] { aplat.Color },
        GradientBrush degrade => degrade.GradientStops.Select(a => a.Color),
        _ => throw new InvalidOperationException($"fond non gere : {ressource?.GetType().Name}")
    };

    private static double Canal(double v)
    {
        v /= 255.0;
        return v <= 0.03928 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c)
        => 0.2126 * Canal(c.R) + 0.7152 * Canal(c.G) + 0.0722 * Canal(c.B);

    private static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (haut, bas) = la > lb ? (la, lb) : (lb, la);
        return (haut + 0.05) / (bas + 0.05);
    }

    // -------------------------------------------------------------------
    // Une liste « tout ce que les vues passent en Foreground » verifiee a la main derive :
    // `SurChromeBrush`, l'encre de la capsule chrome (l'action principale de l'ecran) et du
    // bouton fermer au survol, pourrait en manquer sans que rien ne rougisse.
    //
    // Le risque n'est pas theorique : le theme contient une couleur qui ECHOUE
    // le seuil, `IrisVioletBrush` (4,33:1 sur ObsidienneRelevee). Elle ne sert
    // aujourd'hui que dans des degrades decoratifs. Le jour ou quelqu'un l'ecrit
    // en Foreground, ce fichier resterait vert : elle n'est pas dans la liste.
    //
    // Le crible remplace la verification humaine : il relit les XAML et exige
    // que chaque pinceau reellement employe en Foreground soit couvert ici.
    // -------------------------------------------------------------------
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Chaque pinceau employe en Foreground : attribut direct ou Setter de style.</summary>
    private static SortedSet<string> EncresDuXaml()
    {
        var trouvees = new SortedSet<string>(StringComparer.Ordinal);
        var source = Path.Combine(Racine().FullName, "src", "DrDxt.App");
        foreach (var fichier in Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories))
        {
            var texte = File.ReadAllText(fichier);
            foreach (Match m in Regex.Matches(texte, @"Foreground=""\{StaticResource (\w+)\}"""))
                trouvees.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(
                texte, @"Property=""Foreground""\s+Value=""\{StaticResource (\w+)\}"""))
                trouvees.Add(m.Groups[1].Value);
        }
        return trouvees;
    }

    [Fact]
    public void Le_crible_voit_bien_des_encres_dans_le_xaml()
    {
        // Sans ce temoin, un motif casse rendrait le test suivant vert sur le vide.
        var vues = EncresDuXaml();

        vues.Should().HaveCountGreaterThan(4);
        vues.Should().Contain("TexteBrush");
    }

    [Fact]
    public void Aucune_encre_employee_dans_le_xaml_n_echappe_a_la_mesure()
    {
        var couvertes = new SortedSet<string>(Encres, StringComparer.Ordinal);
        couvertes.UnionWith(EncresSurAccent);

        var oubliees = EncresDuXaml().Except(couvertes).ToList();

        oubliees.Should().BeEmpty(
            "une encre que ce fichier ne mesure pas peut etre illisible sans qu'aucun " +
            "test ne rougisse. Ajoutez-la a Encres (fonds sombres) ou a EncresSurAccent " +
            "(fonds de couleur), avec le fond sur lequel elle est reellement posee");
    }

    [Fact]
    public void L_encre_des_boutons_de_couleur_se_lit_sur_chaque_fond_d_accent()
    {
        // La capsule chrome porte du texte presque noir sur un degrade clair : le
        // contraste y varie d'un bout du degrade a l'autre, et c'est l'action
        // principale de l'ecran -- celle qu'on ne peut pas se permettre d'illisible.
        // Assombrir un arret du degrade suffirait a l'effacer.
        var fautes = StaApplication.Run(() =>
        {
            var liste = new List<string>();
            foreach (var fond in FondsAccent)
            {
                foreach (var cf in CouleursDuFond(System.Windows.Application.Current.Resources[fond]))
                {
                    foreach (var encre in EncresSurAccent)
                    {
                        var ce = ((SolidColorBrush)System.Windows.Application.Current.Resources[encre]).Color;
                        var r = Ratio(ce, cf);
                        if (r < AaTexteNormal)
                            liste.Add($"{encre} sur {fond} ({cf}) : {r:0.00}:1");
                    }
                }
            }
            return liste;
        });

        fautes.Should().BeEmpty();
    }

    [Fact]
    public void Le_calcul_du_ratio_est_lui_meme_verifie()
    {
        // Sans ces deux reperes, le reste du fichier n'est qu'une opinion chiffree.
        Ratio(Colors.Black, Colors.White).Should().BeApproximately(21.0, 0.01);
        Ratio(Color.FromRgb(0x77, 0x77, 0x77), Colors.White).Should().BeApproximately(4.48, 0.01);
    }

    [Fact]
    public void Chaque_encre_du_chrome_se_lit_sur_chaque_fond_du_chrome()
    {
        var fautes = StaApplication.Run(() =>
        {
            var liste = new List<string>();
            foreach (var fond in Fonds)
            {
                var cf = ((SolidColorBrush)System.Windows.Application.Current.Resources[fond]).Color;
                foreach (var encre in Encres)
                {
                    // Une encre en dégradé (le chrome du titre) : chacun de ses arrêts doit se lire.
                    foreach (var ce in CouleursDuFond(System.Windows.Application.Current.Resources[encre]))
                    {
                        var r = Ratio(ce, cf);
                        if (r < AaTexteNormal)
                            liste.Add($"{encre} sur {fond} : {r:0.00}:1");
                    }
                }
            }
            return liste;
        });

        fautes.Should().BeEmpty();
    }
}
