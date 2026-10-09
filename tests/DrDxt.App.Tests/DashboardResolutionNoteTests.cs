using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// « PAS DE GAIN MESURABLE » est un mensonge par omission.
///
/// Le tableau de bord affichait cette seule phrase des que l'ecart ne depassait pas la marge. Elle
/// laisse croire que le changement n'a rien fait. La verite est souvent autre : **l'ecart existe et
/// cette session ne pouvait pas le resoudre**. Le cas reel, mesure le 07/09/2026 sur la RTX 4060 :
/// l'offset `+100/100` rend +2,7 % de calcul GPU reproductible, et le rejeu in-game ne resout que
/// +/-4,8 %. Dire « pas de gain » est faux ; dire « +2,7 % non resolu a +/-4,8 % » est vrai, et
/// c'est la seule formulation qui laisse le joueur decider.
///
/// `AbComparer.Compare` calculait deja cette resolution — dans une variable locale, qui mourait
/// avec la methode. Elle traverse desormais jusqu'a l'ecran.
/// </summary>
public sealed class DashboardResolutionNoteTests
{
    private static BenchmarkResult Bench(double avg, double marge) =>
        new(avg, avg * 0.75, avg * 0.6, new[] { 1000.0 / avg }, 18, marge, Runs: 3);

    private static ApplyReport Rapport(double avantFps, double apresFps, double marge, bool gain, string cle)
    {
        var avant = Bench(avantFps, marge);
        var apres = Bench(apresFps, marge);
        return new ApplyReport(
            new[] { new ApplyOutcome("latency.reflex", true, false, new TweakSnapshot("latency.reflex", "{}"), null) },
            new AbVerdict(avant, apres, gain, cle));
    }

    /// <summary>Des modeles minimaux a la place des vraies traductions : le test verifie QUELLE
    /// phrase est choisie et QUELS nombres y entrent, pas la formulation. Les vraies chaines sont
    /// couvertes par `DashboardLocalizationTests`.</summary>
    private static string Modele(string cle) => cle switch
    {
        "dashboard.resolution.unresolved" => "ecart={0:0.0} resolution={1:0.0}",
        "dashboard.resolution.unknown" => "ecart={0:0.0} resolution inconnue",
        _ => cle,
    };

    private static string Nombre(double v) =>
        v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    private static DashboardViewModel Vm(ApplyReport rapport)
    {
        var vm = new DashboardViewModel(null, null, null, Modele);
        vm.Load(rapport);
        return vm;
    }

    [Fact]
    public void Un_ecart_NON_RESOLU_dit_l_ecart_ET_la_resolution()
    {
        // 137,2 → 140,9 fps, marges 3,3 + 3,3 : +2,70 % pour une resolution de +/-4,81 %.
        var vm = Vm(Rapport(137.2, 140.9, 3.3, gain: false, "verdict.no_measurable_gain"));

        vm.ResolutionNote.Should().NotBeNullOrWhiteSpace(
            "taire la resolution transforme « je n'ai pas pu voir » en « il n'y a rien »");
        // 2,70 % d'ecart pour une resolution de 4,81 % — les deux nombres, pas seulement le verdict.
        vm.ResolutionNote.Should().Contain(Nombre(2.7)).And.Contain(Nombre(4.8));
    }

    [Fact]
    public void Un_gain_PROUVE_n_a_pas_besoin_de_cette_note()
    {
        // La note explique une absence de conclusion. Quand il y a une conclusion, elle encombre.
        var vm = Vm(Rapport(100, 130, 2, gain: true, "verdict.measurable_gain"));

        vm.ResolutionNote.Should().BeNull();
    }

    [Fact]
    public void Une_resolution_INCONNUE_le_dit_au_lieu_d_afficher_l_infini()
    {
        // `InterRunMarginOfError` vaut +∞ sous 3 passes. Afficher « ±∞ % » serait grotesque ;
        // se taire ferait croire a une mesure fine. Il faut une troisieme phrase.
        var vm = Vm(Rapport(100, 120, double.PositiveInfinity, gain: false, "verdict.no_measurable_gain"));

        vm.ResolutionNote.Should().NotBeNullOrWhiteSpace();
        vm.ResolutionNote.Should().NotContain("∞").And.NotContain("Infini");
        vm.ResolutionNote.Should().Contain("inconnue", "il faut une TROISIEME phrase, pas un silence");
    }

    [Fact]
    public void Une_comparaison_INVALIDE_ne_recoit_aucune_note_chiffree()
    {
        // Expositions trop differentes : aucun chiffre n'a de sens, et en afficher un serait pire
        // que de se taire. Le verdict est deja null dans ce cas cote moteur.
        var vm = Vm(new ApplyReport(
            new[] { new ApplyOutcome("latency.reflex", true, false, new TweakSnapshot("latency.reflex", "{}"), null) },
            null));

        vm.ResolutionNote.Should().BeNull();
    }
}
