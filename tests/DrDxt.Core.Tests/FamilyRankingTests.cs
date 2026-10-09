using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le root-cause classe des familles, pas des identifiants.
///
/// C'est le renversement qui compte. Le produit demandait « quel tweak avons-nous sous la
/// main ? » et repondait avec son catalogue — c'est ainsi qu'on obtient « 0 candidat retenu sur 9 »
/// et qu'on le confond avec « cette machine n'a rien a optimiser ». Il demande maintenant « ou
/// cette machine perd-elle de la performance ? », et le catalogue vient apres.
///
/// Une famille peut donc arriver en tete **sans qu'aucun levier n'existe encore pour elle**. C'est
/// meme le resultat le plus utile : il dit quoi construire.
///
/// Mesure du 08/09/2026 sur Warzone : GPU a 88-90 %, frequence plate, aucun plafond publie, et un
/// thread de jeu a 79-91 % d'un thread logique sur trois fenetres consecutives. Ce qui manque
/// n'est pas de la puissance GPU — c'est de quoi l'alimenter.
/// </summary>
public class FamilyRankingTests
{
    private static DiagnosticMachine Machine(
        BottleneckState goulot = BottleneckState.MixedGpuLeaning,
        GpuLimiterObserved bridage = GpuLimiterObserved.RienDeBridant,
        GameThreadPressure pression = GameThreadPressure.Unknown) =>
        new(goulot, bridage, 2715, 2715, TuningGpuAutorise: false, PressionThread: pression);

    [Fact]
    public void UN_THREAD_SOUS_PRESSION_ET_UN_GPU_NON_SATURE_METTENT_LE_CPU_EN_TETE()
    {
        // La situation exacte de cette machine.
        var rang = CandidateSelector.ClasserFamilles(
            Machine(pression: GameThreadPressure.Observee));

        rang[0].Famille.Should().Be(CandidateFamily.Cpu);
        rang[1].Famille.Should().Be(CandidateFamily.Scheduler);
        rang[0].Motif.Should().Contain("sous saturation");
    }

    [Fact]
    public void SANS_MESURE_DE_THREAD_LE_CPU_NE_REMONTE_PAS()
    {
        // Unknown n'est pas « aucune pression ». Une famille ne se classe pas en tete sur une
        // absence de mesure — c'est exactement ainsi qu'on fabrique un diagnostic.
        var rang = CandidateSelector.ClasserFamilles(Machine(pression: GameThreadPressure.Unknown));

        rang.Should().NotContain(x => x.Famille == CandidateFamily.Cpu);
    }

    [Fact]
    public void UN_PLAFOND_OBSERVE_FAIT_REMONTER_LE_THERMIQUE()
    {
        CandidateSelector.ClasserFamilles(Machine(bridage: GpuLimiterObserved.PlafondThermiqueObserve))
            .Should().Contain(x => x.Famille == CandidateFamily.Thermal);
    }

    [Fact]
    public void UN_GPU_QUI_PACE_TOUTE_LA_FRAME_MET_LE_GPU_DANS_LE_CLASSEMENT()
    {
        CandidateSelector.ClasserFamilles(Machine(goulot: BottleneckState.GpuBound))
            .Should().Contain(x => x.Famille == CandidateFamily.Gpu);
    }

    [Fact]
    public void LA_REPARATION_FERME_TOUJOURS_LA_MARCHE_ET_N_EST_JAMAIS_ABSENTE()
    {
        // Elle ne demande aucune mesure de jeu et elle assainit le terrain sur lequel toutes les
        // autres familles mesureront. Elle a donc toujours sa place — jamais en tete d'un
        // diagnostic qui a trouve mieux, jamais absente non plus.
        foreach (var pression in Enum.GetValues<GameThreadPressure>())
        {
            var rang = CandidateSelector.ClasserFamilles(Machine(pression: pression));
            rang.Should().Contain(x => x.Famille == CandidateFamily.Repair);
            rang[^1].Famille.Should().Be(CandidateFamily.Repair);
        }
    }

    [Fact]
    public void CHAQUE_FAMILLE_CLASSEE_PORTE_SON_MOTIF()
    {
        // Un classement sans motif est une autorite : personne ne saurait s'il s'est trompe.
        CandidateSelector.ClasserFamilles(Machine(pression: GameThreadPressure.Observee))
            .Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.Motif));
    }
}
