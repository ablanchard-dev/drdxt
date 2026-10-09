namespace DrDxt.Core;

// --- Scan (free funnel) ---

public enum Severity { Info, Minor, Major }

public record Finding(
    string Category,
    Severity Severity,
    string TitleKey,
    double? EstimatedGainPct);

/// <summary>
/// Honest signals about what was and was not measured in this scan pass.
/// In M1, latency is config-based only (no PresentMon baseline) and stability
/// assessment is limited in depth.
/// </summary>
public record DimensionStatus(
    bool LatencyMeasured,       // false in M1 — no PresentMon baseline latency reading
    bool StabilityFullyAssessed // true once a PresentMon baseline yields a measured frametime-consistency grade; false = config estimate only
);

/// <summary>
/// Per-dimension sub-scores (0–100 each) — DISPLAY GROUPINGS of the same deduction ledger
/// (plan §3), not weighted terms. Each sub-score = clamp(100 − penalties of its group). The
/// overall <see cref="ScanResult.Score"/> is the register total (100 − ALL penalties), not a
/// 50/30/20 weighted average of these three: a point is always attributable to one named lever.
/// </summary>
public record ScoreBreakdown(int Performance, int Latency, int Stability);

/// <summary>
/// State of a scoring lever for a given machine (plan §2.3). Only <see cref="Suboptimal"/>
/// deducts; <see cref="Unknown"/> (unreadable) and <see cref="NotApplicable"/> (hardware/chassis
/// or symptom gating not met) deduct nothing — honesty on incertitude, never an unjust penalty.
/// </summary>
public enum LeverState { Optimal, Suboptimal, Unknown, NotApplicable }

/// <summary>
/// One auditable line of the /100 deduction ledger (plan §2.1, §6.1). Makes the score
/// line-itemizable: each lever names the points it removed (0 unless <see cref="State"/> is
/// <see cref="LeverState.Suboptimal"/>), its display group, and a source key. The magnitudes are
/// Calibrated expert estimates (ordinal Major/Minor tiers), not measured percentages — the only
/// real measurement lives in the separate A/B PresentMon block, never in the /100 (audit C1).
/// </summary>
public record ScoreLedgerEntry(
    string LeverId,
    string Group,        // "Performance" | "Latency" | "Stability" (display bucket)
    int Penalty,         // points actually removed (0 unless State == Suboptimal)
    LeverState State,
    string TitleKey,     // localization key naming the lever
    string SourceKey);   // north-star fiche the tier estimate is drawn from

/// <summary>
/// Honest coverage/confidence over the scored levers (plan §2.4). A high score with many
/// unreadable levers must not pass for a fully-audited machine: <c>Known/Applicable</c> says how
/// much was actually verified. Unknown levers cost 0 points but ARE counted here so the UI can
/// say e.g. "92/100 — based on 9 of 11 detectable levers (2 not read)".
/// </summary>
public record ScanCoverage(int ApplicableLevers, int KnownLevers, int UnknownLevers);

public record ScanResult(
    HardwareInventory Hw,
    SettingsState State,
    IReadOnlyList<Finding> Findings,
    int Score, /*0-100*/
    BenchmarkResult? Baseline,       // nullable = degraded mode if PresentMon unavailable
    ScoreBreakdown Breakdown,        // per-dimension sub-scores (display groupings)
    DimensionStatus DimensionStatus, // honest flags for what was/wasn't measured
    // Optional (trailing, defaulted) so existing positional constructions stay source-compatible.
    IReadOnlyList<ScoreLedgerEntry>? Ledger = null, // per-lever audit of every deducted point
    ScanCoverage? Coverage = null,                  // how many levers were actually readable
    // L1.3 — ce qui te coute des images, tel que la mesure le dit. null = rien n'a ete mesure
    // cette session. Ce verdict ne touche pas au /100 (score de CONFIGURATION) : il decide ce
    // qu'on a le droit de PROMETTRE, et quels leviers cessent d'etre vendus comme des images.
    BottleneckVerdict? Bottleneck = null
);

public interface IScanScorer
{
    // Autoritative (master §10 + vérif adverse A2) : retour wrappé Result<ScanResult>,
    // cohérent avec IHardwareDetector / ISettingsStateReader / IBenchmarkRunner.
    Result<ScanResult> Score(HardwareInventory hw, SettingsState state, BenchmarkResult? baseline);
}
