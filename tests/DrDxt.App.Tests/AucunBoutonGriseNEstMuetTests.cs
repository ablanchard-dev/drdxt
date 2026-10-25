using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le garde STRUCTUREL, ecrit apres avoir corrige quatre boutons un
// par un (Appliquer, Tout annuler, Activer, Rafraichir). Corriger des instances
// ne protege pas la prochaine : la lecon de la journee est qu'une garantie doit
// tenir au SELECTEUR, pas a l'element.
//
// La regle du produit, en une phrase : une commande qui peut se griser doit soit
// DISPARAITRE quand elle n'a pas de sens, soit dire ce qu'elle attend — et son
// info-bulle doit s'afficher meme desactivee, sans quoi WPF la cache exactement
// quand elle sert.
//
// Troisieme possibilite, la pire : la commande peut se griser et n'a aucun bouton.
// La fonctionnalite est alors inatteignable, et rien dans l'interface ne le dit.
// -----------------------------------------------------------------------
public sealed class AucunBoutonGriseNEstMuetTests
{
    /// <summary>
    /// La seule commande grisable sans bouton : le reglage GPU, que personne ne peut declencher
    /// depuis l'ecran. La brancher ou la retirer est une decision produit ; ce test la garde
    /// visible dans le code au lieu de la laisser dans une note qu'on ne relit pas.
    /// </summary>
    private static readonly string[] SansBoutonEnAttenteDArbitrage = ["ApplyGpuTuningCommand"];

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Les commandes declarees avec un CanExecute : celles qui peuvent se griser.</summary>
    private static List<string> CommandesGrisables()
    {
        var vms = Path.Combine(Racine().FullName, "src", "DrDxt.App", "ViewModels");
        var noms = new List<string>();
        foreach (var f in Directory.EnumerateFiles(vms, "*.cs", SearchOption.AllDirectories))
        {
            var texte = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(
                texte,
                @"\[RelayCommand\(CanExecute[^\]]*\]\s*(?:\[[^\]]*\]\s*)*private\s+(?:async\s+)?\w[\w<>\.]*\s+(\w+?)(?:Async)?\s*\("))
            {
                noms.Add(m.Groups[1].Value + "Command");
            }
        }
        return noms;
    }

    private static string TousLesXaml()
    {
        var vues = Path.Combine(Racine().FullName, "src", "DrDxt.App");
        return string.Concat(Directory.EnumerateFiles(vues, "*.xaml", SearchOption.AllDirectories)
                                      .Select(File.ReadAllText));
    }

    [Fact]
    public void Le_crible_voit_bien_les_commandes_grisables()
    {
        // Temoin : un motif qui ne trouverait rien ferait passer tous les tests
        // suivants sur une liste vide, en annoncant une garantie inexistante.
        var trouvees = CommandesGrisables();
        trouvees.Should().HaveCountGreaterThanOrEqualTo(3);
        trouvees.Should().Contain("ApplyCommand")
                .And.Contain("ApplyGpuTuningCommand").And.Contain("InstallUpdateCommand");
    }

    [Fact]
    public void Chaque_commande_grisable_a_un_bouton()
    {
        var xaml = TousLesXaml();
        var orphelines = CommandesGrisables()
            // Un bouton d'une liste atteint sa commande par le DataContext de la liste.
            .Where(c => !xaml.Contains($"Command=\"{{Binding {c}}}\"") && !xaml.Contains($"Command=\"{{Binding DataContext.{c},"))
            .Except(SansBoutonEnAttenteDArbitrage)
            .ToList();

        orphelines.Should().BeEmpty(
            "une commande qui peut se griser et n'a aucun bouton est une fonctionnalite " +
            "inatteignable, et rien dans l'interface ne le dit");
    }

    [Fact]
    public void Chaque_bouton_grisable_disparait_ou_s_explique()
    {
        var xaml = TousLesXaml();
        var muets = new List<string>();

        foreach (var commande in CommandesGrisables().Except(SansBoutonEnAttenteDArbitrage))
        {
            var depart = xaml.IndexOf($"Command=\"{{Binding {commande}}}\"", StringComparison.Ordinal);
            if (depart < 0) continue;

            // Le bloc de la balise <Button …/> qui porte la commande.
            var ouverture = xaml.LastIndexOf('<', depart);
            var fermeture = xaml.IndexOf('>', depart);
            var bloc = xaml[ouverture..(fermeture + 1)];

            var cache = bloc.Contains("Visibility=");
            var explique = bloc.Contains("ToolTipService.ShowOnDisabled=\"True\"")
                           && bloc.Contains("ToolTip=");

            if (!cache && !explique) muets.Add(commande);
        }

        muets.Should().BeEmpty(
            "un bouton gris sans explication ressemble a une application cassee : " +
            "soit il disparait, soit il dit ce qu'il attend (et son info-bulle doit " +
            "s'afficher MEME desactivee)");
    }

    [Fact]
    public void L_exception_reste_une_exception_nommee()
    {
        // Une liste d'exceptions qui grossit est une regle qu'on a cesse de tenir.
        SansBoutonEnAttenteDArbitrage.Should().HaveCount(1);
    }
}
