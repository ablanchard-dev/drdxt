using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Le tuning GPU passe par le même juge que les autres leviers.</summary>
public sealed partial class ScanApplyCommandTests
{
    // ====================== le tuning GPU passe par le meme jugement ======================
    // Le bouton GPU ecrivait sur la carte sans jamais demander au selecteur, alors que le diagnostic
    // porte la decision « rien ne s'ecrit sur la carte depuis l'App » et le veto des arrets brutaux.

    private static ScanProfileConsumption AvecPlanGpu() =>
        ScanViewModelTestSeams.PlanConsumption(System.Array.Empty<string>())
            with { GpuPlan = new TweakPlan(new[] { "gpu.undervolt", "gpu.power_limit" }) };

    private static async Task ScanThenApplyGpu(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Sans_arret_qui_touche_la_carte_le_tuning_GPU_se_mesure_en_jeu_et_jamais_par_lot()
    {
        // Le drapeau global retiré, la carte se règle comme tout levier à chaud : point de
        // restauration, puis la session mesurée en jeu (gardé seulement avec un gain mesuré). L'étape par lot, qui
        // gardait un réglage sans gain, n'est plus jamais appelée.
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu);
        await ScanThenApplyGpu(h);

        await h.Safety.Received(1).EnsureRestorePointAsync();
        h.CandidatsCrees.Should().Equal("gpu.power_limit"); // La fréquence reste fermée (règle de la carte)
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert chez toi");
    }

    [Fact]
    public async Task Optimiser_atteint_la_carte_le_bouton_visible_et_pas_seulement_la_commande_sans_bouton()
    {
        // La commande GPU dédiée n'a aucun bouton ; rouvrir la carte n'a de sens que si
        // « Optimiser » la propose, mesurée en jeu comme le reste.
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.ApercuOptimisation.Should().Contain("tweak.gpu.power_limit.title").And.NotContain("tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Des_arrets_secs_repetes_ouvrent_la_carte_sous_garde_et_la_phrase_le_dit()
    {
        var h = Build(consumption: AvecPlanGpu() with
        {
            Items = [ScanViewModelTestSeams.Item("gpu.undervolt", inPlan: false, intent: ProfileIntent.GpuTune, applicable: true)],
        }, pid: PidDuJeu, arretsRecents: 2);
        await ScanThenApplyGpu(h);

        // La limite de puissance se tente sous garde ;
        // la fréquence et la tension restent hors d'Optimiser (règle de la carte), quels que soient les arrêts.
        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert sous garde").And.Contain("arrêt sec sans code")
            .And.NotContain("inattribuable");
        h.Vm.ApercuOptimisation.Should().NotContain("tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Avec_des_arrets_secs_repetes_le_tuning_GPU_se_tente_sous_garde()
    {
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu, arretsRecents: 3);
        await ScanThenApplyGpu(h);

        // Comme sans arrêt (la session mesurée en jeu, jamais l'étape par lot), et la phrase dit la garde.
        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert sous garde");
    }

    [Fact]
    public async Task Sans_jeu_le_tuning_GPU_n_est_pas_tente()
    {
        var h = Build(consumption: AvecPlanGpu(), pid: 0);
        await ScanThenApplyGpu(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
    }
}
