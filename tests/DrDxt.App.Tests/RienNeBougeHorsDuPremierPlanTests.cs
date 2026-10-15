using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// DrDXT anime : du chrome qui coule, un reseau qui respire. La regle
// est ecrite en tete d'App.xaml : « Tout ce qui bouge obeit a
// Effets/MoteurDAnimation : la fenetre doit etre au premier plan. Pendant une
// mesure en jeu, c'est le jeu qui l'est, et l'interface ne coute rien. »
//
// Cette regle n'est pas une preference esthetique. Une application d'optimisation
// PC qui anime en arriere-plan PREND les images par seconde qu'elle pretend
// rendre -- et elle le fait pendant la mesure, donc elle fausse aussi le chiffre
// qu'elle affiche.
//
// Mesure du jour, sans defaut trouve : aucun effet n'ouvre sa propre horloge.
// Tous appellent `MoteurDAnimation.HorlogePour(...)`, une seule porte, qui
// s'arrete quand la fenetre passe derriere. C'est deja structurel -- mais rien
// n'empechait le prochain effet d'ouvrir la sienne, et un `DispatcherTimer` prive
// ne se voit pas : il tourne, simplement.
// -----------------------------------------------------------------------
public sealed class RienNeBougeHorsDuPremierPlanTests
{
    // Ouvrir une horloge : ces deux formes tournent jusqu'a ce qu'on les arrete.
    private static readonly Regex OuvreUneHorloge =
        new(@"CompositionTarget\.Rendering\s*\+=|new\s+(?:System\.Windows\.Threading\.)?DispatcherTimer");

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Le code tel qu'il est LIVRE : les blocs `#if DEBUG` n'en font pas partie.</summary>
    private static string HorsDebug(string source)
    {
        var garde = new List<string>();
        var profondeur = 0;
        foreach (var ligne in source.Split('\n'))
        {
            var t = ligne.TrimStart();
            if (t.StartsWith("#if DEBUG", StringComparison.Ordinal)) { profondeur++; continue; }
            if (profondeur > 0 && t.StartsWith("#endif", StringComparison.Ordinal)) { profondeur--; continue; }
            if (profondeur == 0) garde.Add(ligne);
        }
        return string.Join('\n', garde);
    }

    private static IEnumerable<FileInfo> Effets()
        => new DirectoryInfo(Path.Combine(Racine().FullName, "src", "DrDxt.App", "Effets"))
            .GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Le_crible_voit_bien_les_effets_et_leur_horloge()
    {
        // Temoin de contenu : sans lui, un chemin change rendrait tout vert sur zero fichier.
        var effets = Effets().ToList();

        effets.Should().HaveCountGreaterThan(2);
        effets.Should().Contain(f => f.Name == "MoteurDAnimation.cs");
        OuvreUneHorloge.IsMatch(File.ReadAllText(
            effets.Single(f => f.Name == "MoteurDAnimation.cs").FullName))
            .Should().BeTrue("le moteur, lui, ouvre bien une horloge : c'est son role");
    }

    [Fact]
    public void Le_crible_reconnait_les_deux_formes_d_horloge()
    {
        // Temoin inverse, sur les deux motifs et sur ce qui ne doit pas mordre.
        OuvreUneHorloge.IsMatch("CompositionTarget.Rendering += Avancer;").Should().BeTrue();
        OuvreUneHorloge.IsMatch("var t = new DispatcherTimer();").Should().BeTrue();
        OuvreUneHorloge.IsMatch("MoteurDAnimation.HorlogePour(this, Avancer);").Should().BeFalse();
    }

    [Fact]
    public void Aucun_effet_n_ouvre_son_horloge_a_lui()
    {
        var rebelles = Effets()
            .Where(f => f.Name != "MoteurDAnimation.cs")
            .Where(f => OuvreUneHorloge.IsMatch(HorsDebug(File.ReadAllText(f.FullName))))
            .Select(f => f.Name)
            .ToList();

        rebelles.Should().BeEmpty(
            "une horloge privee continue de tourner quand la fenetre passe derriere : " +
            "elle prend les images par seconde que ce produit promet de rendre, et fausse " +
            "la mesure en cours. Passez par MoteurDAnimation.HorlogePour");
    }

    [Fact]
    public void La_regle_du_moteur_reste_celle_qu_annonce_le_theme()
    {
        // La garantie positive : le crible ci-dessus protege une porte, encore faut-il
        // que la porte ferme. Trois etats, trois raisons de ne pas animer.
        DrDxt.App.Effets.MoteurDAnimation.Decider(true, false, true).Should().BeTrue();
        DrDxt.App.Effets.MoteurDAnimation.Decider(false, false, true).Should().BeFalse("fenetre derriere");
        DrDxt.App.Effets.MoteurDAnimation.Decider(true, true, true).Should().BeFalse("fenetre reduite");
        DrDxt.App.Effets.MoteurDAnimation.Decider(true, false, false).Should().BeFalse("animations coupees par Windows");
    }

    // Sur ma machine, les animations de Windows sont coupées (un réglage d'optimisation courant) ; tout
    // DrDXT était figé et il ne voyait « rien bouger ». Le joueur peut animer DrDXT malgré Windows ; le premier
    // plan et la fenêtre réduite restent des portes fermées, c'est ce qui protège la mesure en jeu.
    [Fact]
    public void Le_joueur_peut_animer_DrDXT_malgre_Windows_sans_ouvrir_le_premier_plan()
    {
        DrDxt.App.Effets.MoteurDAnimation.Decider(true, false, false, animerMalgreWindows: true).Should().BeTrue();
        DrDxt.App.Effets.MoteurDAnimation.Decider(false, false, false, animerMalgreWindows: true).Should().BeFalse("fenetre derriere");
        DrDxt.App.Effets.MoteurDAnimation.Decider(true, true, false, animerMalgreWindows: true).Should().BeFalse("fenetre reduite");
    }

    // Animations de Windows coupées sur ma machine, l'anneau du scan figé, « ça fait buggé ». L'anneau
    // est un indicateur de travail : il tourne malgré Windows (les portes premier plan / réduite restent dans Reevaluer).
    [Fact]
    public void L_anneau_du_scan_est_un_indicateur_et_tourne_malgre_Windows()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DrDxt.slnx")) && !File.Exists(Path.Combine(dir, "DrDxt.sln")))
            dir = Path.GetDirectoryName(dir)!;
        var controles = File.ReadAllText(Path.Combine(dir, "src", "DrDxt.App", "Effets", "Controles.cs"));
        var arc = controles[controles.IndexOf("class ArcDeProgression", StringComparison.Ordinal)..];
        arc[..arc.IndexOf("public static readonly", StringComparison.Ordinal)].Should().Contain("indicateur: () => true");
        // Le rond central lui-même : son pouls et son onde au travail sont aussi un indicateur.
        controles.Should().Contain("HorlogePour(this, Respirer, indicateur: () => true)");
        // Et la bille de chrome du bouton (06/10/2026 : elle remplace le cerveau, même rôle d'indicateur).
        File.ReadAllText(Path.Combine(dir, "src", "DrDxt.App", "Effets", "BilleDeChrome.cs"))
            .Should().Contain("HorlogePour(this, Avancer, indicateur: () => true)");
    }
}
