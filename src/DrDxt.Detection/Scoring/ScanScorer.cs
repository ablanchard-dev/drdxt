using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Le bilan des leviers d'un scan : constats, registre (ce que chaque levier coûte, d'une seule source,
/// <see cref="LeverCatalog"/>) et couverture (combien ont été lus). La mesure ne change pas le registre ; elle décide
/// seulement ce qu'il est permis de promettre (verdict de goulot).
/// </summary>
public sealed class ScanScorer : IScanScorer
{
    public Result<ScanResult> Score(HardwareInventory hw, SettingsState state, BenchmarkResult? baseline)
    {
        // La mesure entre ici pour la recommandation, pas pour le registre : sans elle, le produit
        // proposerait les memes leviers a une machine GPU-bound a 97 % et a une machine etranglee
        // par la validation memoire.
        //
        // Le verdict de cap vient de l'etat deja lu par le scan : c'est une lecture du pilote, pas une
        // deduction depuis les frames, et une session capee est celle ou l'A/B ment le plus.
        var bottleneck = BottleneckClassifier.Classify(baseline, state.FrameCap);

        var levers = LeverCatalog.Evaluate(hw, state, bottleneck);

        // Les constats viennent du même catalogue (plus le pilier latence, toujours présent).
        var findings = ScoringRules.Evaluate(hw, state);

        // Registre auditable : une ligne par vrai levier noté (occasions neutres exclues).
        var ledger = levers
            .Where(l => l.CountsForCoverage)
            .Select(l => new ScoreLedgerEntry(
                l.LeverId,
                l.Group,
                l.State == LeverState.Suboptimal ? l.Penalty : 0,
                l.State,
                l.TitleKey,
                l.SourceKey))
            .ToList();

        // Couverture : honnêteté sur Unknown. Seuls les vrais leviers notés comptent ; parmi eux, un
        // Unknown est « applicable mais non lu » (jamais NotApplicable, déjà filtrés plus haut).
        var scored = levers.Where(l => l.CountsForCoverage).ToList();
        var applicable = scored.Count;
        var unknown = scored.Count(l => l.State == LeverState.Unknown);
        var coverage = new ScanCoverage(applicable, applicable - unknown, unknown);

        return Result<ScanResult>.Ok(new ScanResult(
            hw, state, findings, baseline, ledger, coverage,
            bottleneck));
    }
}
