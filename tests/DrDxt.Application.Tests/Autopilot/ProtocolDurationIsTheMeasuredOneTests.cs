using DrDxt.Application.Autopilot;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Le protocole du produit est celui qui a été mesuré.
///
/// `DureeParPasse` valait 60 s sur la foi d'une mesure qui comparait trois passes de 10 s a
/// Trois passes de 60 s — six fois moins de temps de jeu d'un cote. Elle confondait la duree d'une
/// passe avec le temps total, et le produit a mesure les jeux pendant des mois avec une resolution
/// deux a trois fois pire que necessaire.
///
/// Rejeu des huit captures Warzone reelles (07/09/2026) : resolution mediane ~3,4 % a 10 s,
/// ~4,8 % a 20 s, ~30 % a 45 s.
///
/// Elle a ensuite valu **15 s** par crainte qu'a 10 s le 1 % low ne repose que sur ~14 frames.
/// Crainte raisonnable, et fausse : mesuree a temps de jeu EGAL (240 s) par `tools/tailbench`,
/// la resolution vraie du 1 % low vaut 8,7 % en 24 fenetres de 10 s contre 10,1 % en 16 fenetres
/// de 15 s. Le nombre de points bat la precision de chaque point. Details et tableau complet dans
/// `docs/PROTOCOLES-DE-MESURE-IN-GAME.md`.
///
/// Ce test existe pour qu'une intuition — « une passe longue parait plus serieuse » — ne ramene
/// pas la constante en arriere sans refaire la mesure.
/// </summary>
public sealed class ProtocolDurationIsTheMeasuredOneTests
{
    [Fact]
    public void La_duree_par_passe_est_celle_que_les_captures_reelles_ont_designee()
    {
        GameOptimizationSession.DureeParPasse.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Elle_ne_descend_pas_sous_le_seuil_ou_le_1_pourcent_low_devient_un_tirage()
    {
        // A 140 fps, 10 s portent ~1 400 frames et le 1 % low ~14 d'entre elles. C'est peu, et le
        // banc dit que c'est malgre tout le meilleur compromis a temps egal — mais descendre
        // encore ferait reposer le percentile sur une poignee de frames.
        GameOptimizationSession.DureeParPasse.TotalSeconds.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Une_session_complete_tient_dans_le_temps_de_jeu_d_un_joueur()
    {
        // 3 passes d'A/A puis au PIRE 24 alternances : 27 passes de 10 s, soit 4 min 30. Et le
        // pire cas est rare — l'arret sequentiel termine des qu'une regression ou un gain de
        // fluidite est prouve.
        var pireCas = GameOptimizationSession.DureeParPasse.TotalSeconds
                      * (3 + GameOptimizationSession.AlternancesMax);
        pireCas.Should().BeLessThan(300);
    }
}
