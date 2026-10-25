using DrDxt.Application.Autopilot;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Le protocole du produit est celui qui a été mesuré.
///
/// Comparer trois passes de 10 s à trois passes de 60 s confond la durée d'une passe avec le temps total de jeu. À temps
/// de jeu égal, le rejeu des huit captures Warzone réelles donne une résolution médiane de ~3,4 % à 10 s, ~4,8 % à 20 s,
/// ~30 % à 45 s ; et mesurée à 240 s par `tools/tailbench`, la résolution vraie du 1 % low vaut 8,7 % en 24 fenêtres de
/// 10 s contre 10,1 % en 16 fenêtres de 15 s. Le nombre de points bat la précision de chaque point. Détails et tableau
/// complet dans `docs/PROTOCOLES-DE-MESURE-IN-GAME.md`.
///
/// Ce test existe pour qu'une intuition — « une passe longue paraît plus sérieuse » — ne déplace pas la constante sans
/// refaire la mesure.
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
        // 3 passes d'A/A, 24 alternances (le protocole va toujours au bout) et jusqu'a 3 passes refaites apres un alt-tab :
        // 30 passes de 10 s, soit 5 minutes au pire pour le levier qu'Optimiser essaie.
        var pireCas = GameOptimizationSession.DureeParPasse.TotalSeconds
                      * (3 + GameOptimizationSession.AlternancesMax + 3);
        pireCas.Should().BeLessThanOrEqualTo(300);
    }
}
