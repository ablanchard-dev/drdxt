using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Le scan fait remonter les recommandations, avec leurs preuves.</summary>
public sealed partial class ScanApplyCommandTests
{
    // ====================== le scan fait remonter les recommandations du profil ======================

    [Fact]
    public async Task Scan_surfaces_profile_recommendations_with_evidence_and_unverified_marking()
    {
        var consumption = ScanViewModelTestSeams.PlanConsumption(
            new[] { "fixit.power_plan" },
            ScanViewModelTestSeams.Item("fixit.power_plan", tested: false, inPlan: true,
                claim: "cooler, steadier", source: "CAHIER §4"),
            ScanViewModelTestSeams.Item("latency.render_queue_cap", tested: false, inPlan: true,
                intent: ProfileIntent.Latency));
        var h = Build(consumption: consumption);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.Recommendations.Should().HaveCount(2);
        h.Vm.Recommendations.Should().Contain(i =>
            i.TweakId == "fixit.power_plan" && i.Claim == "cooler, steadier" && i.Source == "CAHIER §4");
        h.Vm.Recommendations.Should().OnlyContain(i => !i.IsVerified);
        h.Vm.LabelUnverified.Should().Be("UI.Profile.Unverified");

        // L'étiquette « non vérifié » ne dit pas ce qui ne l'est pas, et la même chaîne veut dire
        // « état illisible » côté BIOS. Sur la carte, un réglage proposé
        // non encore validé le dit en clair.
        h.Vm.Domaines.SelectMany(d => d.Proposes).Should().NotBeEmpty()
            .And.OnlyContain(p => p.Valeur == "Carte.Propose.NonValide");

        // Photo (latence) : le titre en gras était tout l'argumentaire, trois lignes. Le titre est le
        // nom du réglage, celui qu'Optimiser annonce ; l'argumentaire passe dessous.
        var propose = h.Vm.Domaines.SelectMany(d => d.Proposes).Single(p => p.Detail.Contains("cooler, steadier"));
        propose.Titre.Should().Be(TweakDisplayKeys.TitleKey("fixit.power_plan"));
    }

    [Fact]
    public async Task Ce_qui_ne_s_applique_pas_ou_ne_sera_jamais_ecrit_n_est_pas_recommande()
    {
        // Scan reel de la machine de reference : 10 « optimisations recommandees »
        // affichees, dont 5 que le catalogue declare NON applicables a cette machine et 2 reglages
        // GPU que la decision de fermeture n'ecrira jamais. L'ecran les recommandait quand meme.
        var consumption = ScanViewModelTestSeams.PlanConsumption(
            new[] { "latency.gameDvrBackgroundOff" },
            ScanViewModelTestSeams.Item("latency.gameDvrBackgroundOff", inPlan: true, intent: ProfileIntent.Latency),
            ScanViewModelTestSeams.Item("latency.hagsOn", inPlan: false, intent: ProfileIntent.Latency, applicable: false),
            ScanViewModelTestSeams.Item("gpu.undervolt", inPlan: false, intent: ProfileIntent.GpuTune, applicable: true));
        var h = Build(consumption: consumption);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.Recommendations.Select(i => i.TweakId).Should().NotContain("latency.hagsOn",
            "un levier que le catalogue declare inapplicable n'est pas une recommandation");
        // La carte est ouverte machine par machine ; sans arret qui la touche, elle se recommande.
        h.Vm.Recommendations.Select(i => i.TweakId).Should().Contain("gpu.undervolt");
        h.Vm.Recommendations.Select(i => i.TweakId).Should().Contain("latency.gameDvrBackgroundOff");
    }

    [Fact]
    public async Task Sans_recommandation_l_ecran_le_dit_en_clair()
    {
        // Sur la machine de reference, plus aucun levier logiciel n'est recommande. L'ecran
        // affichait « Optimisations recommandees 0 » et une liste vide, sans une phrase.
        var h = Build(consumption: ScanViewModelTestSeams.EmptyConsumption());
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.AucuneRecommandation.Should().BeTrue();
        h.Vm.LabelAucuneRecommandation.Should().Be("Scan.Recommendations.None");
    }

    [Fact]
    public async Task Empty_profile_plan_blocks_with_nothing_to_apply_message()
    {
        var h = Build(consumption: ScanViewModelTestSeams.EmptyConsumption());
        await ScanThenApply(h);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().NotBeNullOrEmpty();
    }
}
