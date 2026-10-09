using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Un bouton dont tout le contenu est un trace geometrique n'a rien
// a dire a un lecteur d'ecran : il s'annonce « bouton », sans plus. C'est
// exactement le defaut du texte alternatif manquant sur une image -- le dessin
// porte le sens, et le sens ne sort pas du dessin.
//
// Mesure du jour : quatre boutons dans ce cas, tous dans MainWindow -- reduire,
// agrandir, restaurer, fermer. Ce sont les seuls gestes qui ferment
// l'application. Les dix-sept autres boutons de l'interface portent un texte.
// -----------------------------------------------------------------------
// Une seule `Application` par domaine : ce fichier construit la fenetre reelle, il
// partage donc la collection des tests WPF plutot que de courir a cote d'eux.
[Collection(CollectionWpf.Nom)]
public sealed class AucunBoutonSansTexteNEstAnonymeTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static IEnumerable<string> Vues()
    {
        var app = Path.Combine(Racine().FullName, "src", "DrDxt.App");
        return Directory.EnumerateFiles(Path.Combine(app, "Views"), "*.xaml")
                        .Append(Path.Combine(app, "MainWindow.xaml"))
                        .Where(File.Exists);
    }

    // 19/09/2026 — ce crible ne connaissait que deux formes de bouton.
    //
    // Mesure : les vues portent aussi deux `ToggleButton`, les en-tetes repliables de
    // l'ecran de licence et de l'ecran de scan. Elles parlent aujourd'hui — chacune
    // contient un `TextBlock` lie — mais elles etaient hors de portee : un en-tete
    // repliable reduit a son chevron est le dessin le plus naturel du monde, et ce
    // fichier serait reste vert en l'annoncant « bouton bascule », sans plus.
    //
    // La liste n'est plus celle des types qu'on a en tete : c'est celle de tout ce qui
    // se clique. Un `CheckBox` ou un `RadioButton` ajoute demain tombe sous la meme
    // regle sans qu'on ait a y penser.
    private static readonly string[] TypesCliquables =
        ["Button", "RepeatButton", "ToggleButton", "CheckBox", "RadioButton"];

    private static IEnumerable<XElement> Boutons(XDocument doc)
        => doc.Descendants().Where(e => TypesCliquables.Contains(e.Name.LocalName)
                                        && e.Name.Namespace == Wpf);

    /// <summary>Un bouton parle s'il porte un texte, ou un nom pose pour l'accessibilite.</summary>
    private static bool Parle(XElement bouton)
    {
        if (bouton.Attribute("AutomationProperties.Name") is not null) return true;
        if (bouton.Attribute("Content") is not null) return true;
        return bouton.Descendants().Any(e => e.Name == Wpf + "TextBlock" || e.Name == Wpf + "AccessText");
    }

    private static string Designation(XElement bouton)
        => bouton.Attribute(Xaml + "Name")?.Value
           ?? bouton.Attribute("Command")?.Value
           ?? "(bouton sans nom ni commande)";

    [Fact]
    public void Le_crible_voit_bien_les_boutons_de_l_interface()
    {
        // Sans ce temoin, un espace de noms mal ecrit rendrait le test suivant muet.
        var total = Vues().Sum(v => Boutons(XDocument.Load(v)).Count());

        total.Should().BeGreaterThan(10);
    }

    [Fact]
    public void Le_crible_distingue_un_bouton_qui_parle_d_un_bouton_muet()
    {
        // Temoin inverse : trois formes de parole, et le silence.
        var doc = XDocument.Parse(
            """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Button Content="Analyser"/>
              <Button><TextBlock Text="Appliquer"/></Button>
              <Button AutomationProperties.Name="Fermer"><Path Data="M0 0 L9 9"/></Button>
              <Button><Path Data="M0 0 L9 9"/></Button>
            </StackPanel>
            """);
        var boutons = Boutons(doc).ToList();

        boutons.Take(3).Should().OnlyContain(b => Parle(b));
        Parle(boutons[3]).Should().BeFalse("un trace geometrique ne dit rien a voix haute");
    }

    [Fact]
    public void Rien_ne_se_clique_EN_DEHORS_de_cette_liste()
    {
        // La liste ci-dessus est complete tant qu'aucun autre element ne repond a la
        // souris. Un `Border` avec un `MouseLeftButtonDown` serait pire qu'un bouton
        // muet : il n'a pas de nom, pas de role, et on ne peut meme pas l'atteindre au
        // clavier. Mesure du 19/09 : il n'y en a aucun — et ce test le maintient.
        var fautifs = new List<string>();
        foreach (var vue in Vues())
        {
            foreach (var element in XDocument.Load(vue).Descendants())
            {
                if (TypesCliquables.Contains(element.Name.LocalName)) continue;

                var souris = element.Attributes()
                    .Where(a => a.Name.LocalName.Contains("MouseLeftButtonDown", StringComparison.Ordinal)
                                || a.Name.LocalName.Contains("MouseDown", StringComparison.Ordinal))
                    .Select(a => a.Name.LocalName);
                foreach (var nom in souris)
                    fautifs.Add($"{Path.GetFileName(vue)} : <{element.Name.LocalName} {nom}=...>");
            }
        }

        fautifs.Should().BeEmpty(
            "un element qui repond a la souris sans etre un bouton n'a ni nom, ni role, "
            + "et ne s'atteint pas au clavier");
    }

    [Fact]
    public void Le_crible_reconnait_un_cliquable_qui_n_est_pas_un_bouton()
    {
        // Temoin inverse, et il a une raison d'etre synthetique : poser un vrai
        // `MouseLeftButtonDown` dans une vue ne COMPILE pas tant que le gestionnaire
        // n'existe pas dans le code-behind. La mutation est donc refusee avant les
        // tests -- une protection reelle, mais qui ne prouve pas que ce crible voit.
        // Ici, le document est parse tel quel, et le crible doit le trouver.
        var doc = XDocument.Parse(
            """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        MouseLeftButtonDown="Quelque chose">
              <Button Content="Analyser"/>
            </StackPanel>
            """);

        var cliquables = doc.Descendants()
            .Where(e => !TypesCliquables.Contains(e.Name.LocalName))
            .SelectMany(e => e.Attributes())
            .Count(a => a.Name.LocalName.Contains("MouseLeftButtonDown", StringComparison.Ordinal));

        cliquables.Should().Be(1, "un panneau qui repond a la souris doit etre vu");
    }

    [Theory]
    [InlineData("Window.Minimize")]
    [InlineData("Window.Maximize")]
    [InlineData("Window.Restore")]
    [InlineData("Window.Close")]
    public void Le_nom_parle_vient_du_catalogue_et_pas_de_la_cle(string cle)
    {
        // Un nom lie a une cle absente serait « vert » pour le crible ci-dessous et
        // annoncerait « Window point Close » a voix haute. ResxLocalizer rend la cle
        // elle-meme quand elle manque : c'est exactement ce qu'on refuse ici.
        var localise = new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle);

        localise.Should().NotBe(cle);
        localise.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Sur_la_fenetre_reelle_les_quatre_boutons_s_annoncent()
    {
        // Le XAML ne prouve rien tant que `StaticResource Loc` n'a pas ete resolu : la
        // ressource est declaree sur la fenetre, les boutons vivent dans son template.
        // Ici on construit la fenetre pour de vrai et on ecoute ce qu'elle dit.
        var noms = StaApplication.Run(() =>
        {
            var fenetre = new DrDxt.App.MainWindow();
            fenetre.ApplyTemplate();
            return LesBoutons(fenetre)
                .Select(System.Windows.Automation.AutomationProperties.GetName)
                .ToList();
        });

        noms.Should().HaveCount(4);
        noms.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("Window."));
    }

    private static IEnumerable<System.Windows.Controls.Button> LesBoutons(System.Windows.DependencyObject racine)
    {
        var enfants = System.Windows.Media.VisualTreeHelper.GetChildrenCount(racine);
        for (var i = 0; i < enfants; i++)
        {
            var enfant = System.Windows.Media.VisualTreeHelper.GetChild(racine, i);
            if (enfant is System.Windows.Controls.Button bouton) yield return bouton;
            foreach (var petit in LesBoutons(enfant)) yield return petit;
        }
    }

    [Fact]
    public void Aucun_bouton_sans_texte_ne_reste_anonyme()
    {
        var muets = Vues()
            .SelectMany(v => Boutons(XDocument.Load(v))
                .Where(b => !Parle(b))
                .Select(b => $"{Path.GetFileName(v)} :: {Designation(b)}"))
            .ToList();

        muets.Should().BeEmpty(
            "un bouton qui n'affiche qu'un trace ne s'annonce que « bouton » : posez " +
            "AutomationProperties.Name, comme on pose un texte alternatif sur une image");
    }
}
