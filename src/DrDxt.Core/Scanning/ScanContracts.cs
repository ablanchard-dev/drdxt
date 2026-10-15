namespace DrDxt.Core;

// --- Scan (parcours gratuit) ---

public enum Severity { Info, Minor, Major }

public record Finding(
    string Category,
    Severity Severity,
    string TitleKey,
    double? EstimatedGainPct);

/// <summary>
/// État d'un levier noté sur une machine donnée. Seul <see cref="Suboptimal"/> retire des
/// points ; <see cref="Unknown"/> (illisible) et <see cref="NotApplicable"/> (condition de
/// matériel, châssis ou symptôme non remplie) ne retirent rien : honnêteté sur l'incertitude, jamais de pénalité injuste.
/// </summary>
public enum LeverState { Optimal, Suboptimal, Unknown, NotApplicable }

/// <summary>
/// Une ligne du registre des leviers : le levier, son groupe d'affichage, son coût quand il est lu et sous-optimal (0
/// sinon) et une clé de source. Les coûts sont des estimations d'expert étalonnées (paliers ordinaux Major/Minor), pas
/// des pourcentages mesurés ; la seule vraie mesure vit dans le bloc A/B PresentMon, à part.
/// </summary>
public record ScoreLedgerEntry(
    string LeverId,
    string Group,        // "Performance" | "Latency" | "Stability" (groupe d'affichage)
    int Penalty,         // points vraiment retirés (0 sauf si State == Suboptimal)
    LeverState State,
    string TitleKey,     // clé de traduction qui nomme le levier
    string SourceKey);   // fiche north-star d'où vient l'estimation du palier

/// <summary>
/// Couverture des leviers : <c>Known/Applicable</c> dit combien a vraiment été lu. Les leviers Unknown ne coûtent rien
/// mais sont comptés, pour que l'écran puisse dire « 9 leviers lus sur 11 (2 non lus) ».
/// </summary>
public record ScanCoverage(int ApplicableLevers, int KnownLevers, int UnknownLevers);

public record ScanResult(
    HardwareInventory Hw,
    SettingsState State,
    IReadOnlyList<Finding> Findings,
    BenchmarkResult? Baseline,       // null = mode dégradé si PresentMon est indisponible
    // Facultatif (en dernier, avec défaut) : les constructions positionnelles existantes compilent toujours.
    IReadOnlyList<ScoreLedgerEntry>? Ledger = null, // audit par levier de chaque point retiré
    ScanCoverage? Coverage = null,                  // combien de leviers étaient vraiment lisibles
    // Ce qui te coute des images, tel que la mesure le dit. null = rien n'a ete mesure
    // cette session. Ce verdict ne touche pas au registre (la configuration) : il decide ce
    // qu'on a le droit de promettre, et quels leviers cessent d'etre vendus comme des images.
    BottleneckVerdict? Bottleneck = null
);

public interface IScanScorer
{
    // Autoritative (vérif adverse) : retour wrappé Result<ScanResult>,
    // cohérent avec IHardwareDetector / ISettingsStateReader / IBenchmarkRunner.
    Result<ScanResult> Score(HardwareInventory hw, SettingsState state, BenchmarkResult? baseline);
}
