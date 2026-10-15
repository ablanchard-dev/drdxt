using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// À temps de jeu égal, des passes plus courtes résolvent mieux.
///
/// Comparer trois passes courtes a trois passes longues (« 3 × 10 s → seuil 22 % ; 3 × 60 s → seuil
/// 1,6 % ») confond la duree d'une passe avec le temps total : six fois moins de temps de jeu d'un cote.
///
/// L'arithmetique tranche : la marge inter-passes vaut `t95(n-2) × σ / √n`. Passer de 3 a 17
/// passes fait chuter t95 de 4,30 a 2,13 **et** multiplie √n par 2,4. Meme si σ grossit sur des
/// passes plus courtes, le produit des deux facteurs gagne largement.
///
/// Mesure le 07/09/2026 en rejouant les huit captures Warzone reelles : mediane ~3,4 % a 10 s
/// contre ~4,8 % a 20 s et ~30 % a 45 s, sept captures sur huit meilleures a 10 s.
/// Voir `docs/PROTOCOLES-DE-MESURE-IN-GAME.md`.
///
/// Ce test grave l'arithmetique, pour qu'aucune intuition ne ramene la constante a 60 s.
/// </summary>
public class ShorterPassesResolveBetterTests
{
    /// <summary>Une scene bruitee, echantillonnee en `n` passes. Le bruit par passe grossit quand
    /// les passes raccourcissent (moins de frames moyennees) — c'est l'effet qui joue contre les
    /// passes courtes, et il est modelise ici pour que le test ne triche pas.</summary>
    private static IReadOnlyList<double> Passes(int n, double bruitBase, int graine)
    {
        // sigma croit en 1/racine(duree) : deux fois moins de temps par passe, sigma x racine(2).
        var sigma = bruitBase * Math.Sqrt(n / 3.0);
        var alea = new Random(graine);
        return Enumerable.Range(0, n)
            .Select(_ => 100.0 + (alea.NextDouble() * 2 - 1) * sigma)
            .ToList();
    }

    [Fact]
    public void A_TEMPS_EGAL_PLUS_DE_PASSES_COURTES_RESOUT_MIEUX_QUE_TROIS_LONGUES()
    {
        // Meme temps de jeu total : 3 passes longues contre 17 passes courtes, avec un bruit par
        // passe qui grossit realistement quand la passe raccourcit.
        var longues = SceneResolution.Probe(Passes(3, bruitBase: 2.0, graine: 1));
        var courtes = SceneResolution.Probe(Passes(17, bruitBase: 2.0, graine: 1));

        courtes.ResolutionPct.Should().BeLessThan(longues.ResolutionPct,
            "t95 chute de 4,30 a 2,13 et racine(n) est multiplie par 2,4 : le produit gagne");
    }

    [Fact]
    public void TROIS_PASSES_EST_LE_PIRE_CAS_POSSIBLE_ET_LE_PRODUIT_DOIT_LE_SAVOIR()
    {
        // A trois passes, t95 vaut 4,303 — plus du double de sa valeur asymptotique. C'est le
        // minimum pour avoir une resolution du tout, et c'est aussi le plus mauvais endroit ou se
        // tenir. Le produit en demande davantage des qu'il peut.
        var trois = SceneResolution.Probe(Passes(3, 2.0, 7));
        var cinq = SceneResolution.Probe(Passes(5, 2.0, 7));

        trois.Verdict.Should().Be(SceneVerdict.Usable);
        cinq.ResolutionPct.Should().BeLessThan(trois.ResolutionPct);
    }

    [Fact]
    public void La_duree_de_passe_du_produit_reste_celle_qui_a_ete_MESUREE()
    {
        // La garde qui empeche le retour a 60 s « parce qu'une passe longue parait plus serieuse ».
        // Le changer demande de refaire la mesure, pas de changer d'avis : la mesure du 07/09/2026,
        // a temps de jeu egal, donne 10 s.
        DrDxt.Core.Tests.ProtocoleReference.DureeParPasseSecondes.Should().Be(10);
    }
}

/// <summary>Le chiffre du protocole, isole pour que le test de Core n'ait pas a dependre de la
/// couche Application. La valeur reelle vit dans `GameOptimizationSession.DureeParPasse`, et
/// `ProtocolDurationIsTheMeasuredOneTests` (Application) verifie que les deux concordent.</summary>
internal static class ProtocoleReference
{
    public const int DureeParPasseSecondes = 10;
}
