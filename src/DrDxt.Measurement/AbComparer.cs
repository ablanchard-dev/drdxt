using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Verdict A/B (§4 P1 + plan 02-M2 C2). The regression criterion is PRIMARILY
/// frame consistency (1% lows), not average FPS alone:
///   • Regression (→ auto-revert) when 1% lows are worse beyond the combined margin
///     (frame consistency degraded) OR avg FPS is worse beyond the combined margin.
///     Frame consistency is the primary trigger, so it takes priority over an avg-FPS
///     nudge that stays within the margin.
///   • Measurable gain when 1% lows OR avg FPS are BETTER beyond the combined margin
///     (and there is no regression).
///   • No measurable gain otherwise — including the key efficiency-tweak outcome:
///     avg FPS within margin AND 1% lows held or improved (an undervolt is ~0 on avg
///     FPS by design). That is a VALID kept state, never auto-reverted.
/// Latency (EstimatedPcLatencyMs) stays informative — not a trigger for auto-revert.
/// </summary>
public static class AbComparer
{
    /// <summary>Ecart maximal d'exposition entre deux passes A/B avant que la comparaison ne soit
    /// declaree invalide : dix points de trames gardees.
    ///
    /// **NON CHIFFRÉ** — ce seuil n'est pas mesure, et il est ecrit ici pour etre trouve et
    /// discute plutot que dilue dans une condition. Ce qui est mesure l'encadre : sur les 2 170
    /// secondes de jeu reel du 07/09/2026, l'hygiene retire **0 %**, donc deux passes propres
    /// valent toutes deux 1,0 et sont a zero d'ecart. Dix points laissent passer une seconde de
    /// chargement d'un cote et pas de l'autre, et arretent un alt-tab. La direction du doute est
    /// choisie : mieux vaut refuser une comparaison douteuse que publier un gain fabrique par une
    /// difference de session.</summary>
    public const double ExpositionComparableMax = 0.10;

    public static AbVerdict Compare(BenchmarkResult before, BenchmarkResult after)
    {
        var combinedMargin = before.MarginOfError + after.MarginOfError;
        // 26/09/2026 : les lows se jugent avec LEUR marge, pas celle des FPS moyens (plus etroite).
        var combinedLowMargin = before.P1LowMarginOfError + after.P1LowMarginOfError;

        var avgDelta = after.AvgFps - before.AvgFps;
        var lowDelta = after.P1LowFps - before.P1LowFps;

        // Frame consistency (1% lows) is the PRIMARY signal; a 1%-low regression beyond
        // the margin wins even if avg FPS edged up within the margin. An avg-FPS loss
        // beyond the margin is also a regression.
        var lowsRegressed = lowDelta < -combinedLowMargin;
        var avgRegressed = avgDelta < -combinedMargin;

        // A gain on EITHER 1% lows or avg FPS, beyond the margin.
        var lowsGained = lowDelta > combinedLowMargin;
        var avgGained = avgDelta > combinedMargin;

        string key;
        bool gain;
        if (lowsRegressed || avgRegressed)
        {
            gain = false;
            key = "Scan.Ab.Regression";
        }
        else if (lowsGained || avgGained)
        {
            gain = true;
            key = "Scan.Ab.MeasurableGain";
        }
        else
        {
            gain = false;
            key = "Scan.Ab.NoMeasurableGain";
        }

        return new AbVerdict(before, after, gain, key);
    }

    /// <summary>
    /// Le meme a/b, mais qui garde sa resolution.
    ///
    /// <see cref="Compare"/> calcule `combinedMargin`, s'en sert, et **la jette** : elle vit
    /// dans une variable locale et meurt avec la methode. C'est pourtant elle qui distingue
    /// « aucun gain » de « gain non resolu par cette session » — et le produit ne savait pas le
    /// dire. Sur la machine de reference, cette confusion a fait passer un +2,7 % reel pour un
    /// zero, alors que la session ne resolvait que +/-4,8 %.
    ///
    /// Rend une <see cref="GameEvidence"/> sur les FPS moyens : l'ecart en pourcent et ce que
    /// l'experience savait resoudre, cote a cote. Le sous-systeme reste null — c'est au banc
    /// deterministe de le remplir, avec sa resolution a lui, bien plus fine.
    ///
    /// La resolution est la SOMME des deux marges, comme dans <see cref="Compare"/>. La chaine
    /// memoire, elle, utilise la racine de la somme des carres. La somme est plus large, donc plus
    /// prudente ; les aligner serait changer un seuil, et un seuil ne se change pas sans mesure.
    /// </summary>
    public static PerformanceEvidence Evidence(BenchmarkResult before, BenchmarkResult after)
    {
        // Pas de reference = pas de pourcentage. Un ecart relatif a zero est infini, donc un
        // verdict tire au sort. Ce n'est pas « pas de changement » : c'est une experience qui ne
        // tient pas, et les deux ne se confondent jamais.
        if (!(before.AvgFps > 0) || !double.IsFinite(before.AvgFps) || !double.IsFinite(after.AvgFps))
            return new PerformanceEvidence(
                null,
                new GameEvidence("fps.moyen",
                    Core.Evidence.Invalide("aucune référence exploitable dans la passe d'avant")));

        // Les deux passes ont-elles vecu la meme chose ? Sans cette question, un A/B compare
        // deux sessions differentes et rend un verdict sans broncher. Le cas concret : l'hygiene
        // retire 2 % de la passe avant et 45 % de la passe apres parce que le joueur a alt-tabbe.
        // C'est observe, pas infere -- l'exposition vient du masque d'hygiene, pas d'une heuristique.
        if (before.HygieneKeptFraction is { } avant && after.HygieneKeptFraction is { } apres
            && Math.Abs(avant - apres) > ExpositionComparableMax)
            return new PerformanceEvidence(
                null,
                new GameEvidence("fps.moyen", Core.Evidence.Invalide(
                    F($"exposition trop différente entre les deux passes ({avant:P0} contre {apres:P0}) : ")
                    + "elles n'ont pas mesuré la même session")));

        var deltaPct = (after.AvgFps - before.AvgFps) / before.AvgFps * 100.0;
        var resolutionPct = (before.MarginOfError + after.MarginOfError) / before.AvgFps * 100.0;

        return new PerformanceEvidence(
            null,
            new GameEvidence("fps.moyen", Core.Evidence.From(deltaPct, resolutionPct)));
    }
}
