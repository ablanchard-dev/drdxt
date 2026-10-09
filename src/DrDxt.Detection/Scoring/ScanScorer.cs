using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// The /100 CONFIG score (plan 11). Deduction ledger: Score = clamp(100 − Σ penalties of the
/// Applicable, known, suboptimal levers, 0, 100). One source of truth (<see cref="LeverCatalog"/>)
/// feeds both the findings and the penalties, so the announced gain == the deducted points.
///
///  - The three sub-scores are DISPLAY GROUPINGS (100 − group penalties), not a 50/30/20 weighted
///    average; the global is the flat register total (plan §3).
///  - Unknown / NotApplicable deduct nothing; Unknown is exposed via Coverage (plan §2.4).
///  - The MEASURED block (PresentMon A/B + margin) is kept strictly OUT of the /100; it rides in
///    <see cref="ScanResult.Baseline"/> with its own margin (plan §1, §4) — never a FPS in the /100.
/// </summary>
public sealed class ScanScorer : IScanScorer
{
    public Result<ScanResult> Score(HardwareInventory hw, SettingsState state, BenchmarkResult? baseline)
    {
        // L1.3 — le maillon qui n'existait pas. `LeverCatalog.Evaluate` ne recevait jamais de
        // mesure, et le commentaire de ce fichier erigeait meme la separation en regle. Elle
        // reste vraie pour le SCORE (le /100 est un score de configuration, la mesure en est
        // tenue a l'ecart) et devient fausse pour la RECOMMANDATION : sans elle, le produit
        // proposait les memes leviers a une machine GPU-bound a 97 % et a une machine etranglee
        // par la validation memoire.
        //
        // Le verdict de cap vient de l'etat deja lu par M1 : c'est une lecture du pilote, pas une
        // deduction depuis les frames, et une session capee est celle ou l'A/B ment le plus.
        var bottleneck = BottleneckClassifier.Classify(baseline, state.FrameCap);

        var levers = LeverCatalog.Evaluate(hw, state, bottleneck);

        // Findings come from the same catalog (plus the always-on latency pillar).
        var findings = ScoringRules.Evaluate(hw, state);

        // NS-6 §1/§7: when a real PresentMon baseline exists, the Stability dimension stops being a
        // pure config estimate and becomes MEASURED — % of frame-TIME that was smooth (100 − the
        // CapFrameX-style stutter %). Conservative min() so a measured stall can lower it, but a real
        // config misconfig (e.g. core parking) is never papered over by a smooth capture. No baseline
        // → the config estimate stands and the dimension is flagged not-fully-assessed (below).
        var configStability = SubScore(levers, LeverCatalog.GroupStability);
        var measuredStability = baseline?.StutterTimePercent is { } stutterPct
            ? (int?)(int)Math.Round(Math.Clamp(100 - stutterPct, 0, 100))
            : null;

        // Per-group sub-scores (display buckets) + global = 100 − ALL penalties (register).
        var breakdown = new ScoreBreakdown(
            Performance: SubScore(levers, LeverCatalog.GroupPerformance),
            Latency: SubScore(levers, LeverCatalog.GroupLatency),
            Stability: measuredStability is { } m ? Math.Min(configStability, m) : configStability);

        var totalPenalty = levers.Where(l => l.State == LeverState.Suboptimal).Sum(l => l.Penalty);
        var overall = Math.Clamp(100 - totalPenalty, 0, 100);

        // Auditable ledger — one line per real scored lever (neutral opportunities excluded).
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

        // Coverage — honesty over Unknown. Only the real scored levers count; among those, an
        // Unknown is "applicable but not read" (never NotApplicable, those are filtered out above).
        var scored = levers.Where(l => l.CountsForCoverage).ToList();
        var applicable = scored.Count;
        var unknown = scored.Count(l => l.State == LeverState.Unknown);
        var coverage = new ScanCoverage(applicable, applicable - unknown, unknown);

        var dimensionStatus = new DimensionStatus(
            LatencyMeasured: baseline?.EstimatedPcLatencyMs != null,
            // Fully assessed once a PresentMon baseline yields a measured consistency grade (above);
            // with no baseline it stays a config estimate and the humility lives in Coverage.
            StabilityFullyAssessed: measuredStability != null);

        return Result<ScanResult>.Ok(new ScanResult(
            hw, state, findings, overall, baseline, breakdown, dimensionStatus, ledger, coverage,
            bottleneck));
    }

    private static int SubScore(IReadOnlyList<LeverCatalog.LeverResult> levers, string group)
    {
        var penalty = levers
            .Where(l => l.Group == group && l.State == LeverState.Suboptimal)
            .Sum(l => l.Penalty);
        return Math.Clamp(100 - penalty, 0, 100);
    }
}
