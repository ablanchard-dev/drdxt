namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Le classifieur de goulot : contrats.
//
// Le manque que ça comble, dit franchement : `LeverCatalog.Evaluate(HardwareInventory,
// SettingsState)` ne recevait jamais de `BenchmarkResult`. Le produit mesurait d'un côté et
// recommandait de l'autre, sans que les deux se parlent : il proposait donc les mêmes leviers
// à une machine GPU-bound à 97 % et à une machine étranglée par la validation mémoire.
//
// La table de décision vient de la note de conception du classifieur (voir `BottleneckClassifier`),
// elle-même dérivée de la sémantique des métriques PresentMon. Ce que la spec dit d'elle-même,
// et qu'il ne faut pas oublier en lisant les constantes ci-dessous :
//
//   « No canonical public threshold says "GPU Busy < X means CPU-bound" for every game. The
//     thresholds above are DrDXT policy thresholds derived from PresentMon metric semantics,
//     not vendor gospel. »
//
// Ce sont donc des seuils de politique, pas des constantes de la nature. Chacun est nommé, et
// reste un bouton de calibration.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// L'état mesuré d'une session : qui donne le tempo, et donc ce qu'il est honnête de proposer.
/// Ensemble fermé : les dix états de la table de décision.
/// </summary>
public enum BottleneckState
{
    /// <summary>Grandeurs absentes, insuffisantes, ou mesure non fiable. L'état honnête par
    /// défaut : aucune réclamation de gain n'est permise dans cet état.</summary>
    UnknownPartial,

    /// <summary>Le GPU pace quasiment toute la frame.</summary>
    GpuBound,

    /// <summary>Majoritairement GPU, mais pas assez pour l'affirmer. Le mot est « peut aider ».</summary>
    MixedGpuLeaning,

    /// <summary>Une part notable de la frame se joue hors GPU : CPU ou application.</summary>
    CpuBoundOrAppBound,

    /// <summary>Franchement CPU/app : le GPU est largement inoccupé.</summary>
    CpuBoundStrong,

    /// <summary>La validation mémoire est proche de sa limite. Problème de fiabilité, pas de FPS.</summary>
    RamCommitPressure,

    /// <summary>La moyenne est correcte mais la cadence ne l'est pas : c'est ce qui se ressent.</summary>
    FramePacing,

    /// <summary>Le débit est là ; c'est la latence qui ne va pas.</summary>
    LatencyOnly,

    /// <summary>Le GPU est bridé par la température ou la puissance.</summary>
    ThermalOrPowerThrottle,

    /// <summary>La cadence est verrouillée par un cap / la vsync : la mesure ne peut rien prouver.</summary>
    CappedOrVsyncLimited,
}

/// <summary>Le degré de confiance du verdict. La copie affichée doit dire « probablement »
/// tant qu'on n'est pas en <see cref="High"/> : c'est une règle de la spec, pas un style.</summary>
public enum BottleneckConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Le verdict : l'état, sa confiance, et son motif en clair.
///
/// Le motif n'est pas décoratif. Sans lui, un utilisateur à qui l'on supprime un levier ne sait
/// pas pourquoi, et nous ne savons pas non plus si le classifieur s'est trompé. Il porte les
/// chiffres qui ont décidé.
/// </summary>
/// <param name="State">L'état retenu.</param>
/// <param name="Confidence">La confiance, jamais High sur une mesure d'un seul run.</param>
/// <param name="Reason">Le motif en clair, avec les grandeurs qui ont tranché.</param>
/// <param name="Provisional">
/// Vrai quand le verdict repose sur moins de runs que la spec n'en exige pour une affirmation
/// forte. La spec est explicite : « Strong claims require 3-run repeatability ». Un verdict
/// provisoire s'affiche, mais ne doit jamais servir à garder ou annuler un réglage.
/// </param>
/// <summary>
/// Les motifs que le classifieur peut rendre, en clés et non en phrases.
///
/// Mesuré le 05/09/2026, en usage réel : le rapport SAV (français, destiné à être collé dans un
/// salon public) affichait « Motif : No measurement was taken, so nothing is claimed about what
/// limits this machine. » Le classifieur construisait ses phrases en anglais et le rapport les
/// recopiait. Huit phrases pouvaient atteindre cette ligne.
///
/// Une clé + ses valeurs, jamais une phrase construite : c'est ce qui permet à chaque bord
/// (rapport, interface, journal de support) de dire la même chose dans sa langue, sans que le
/// classifieur ait à choisir pour eux. Le dépôt le fait déjà pour la couverture GPU
/// (<c>Coverage.Reason.*</c>) ; ceci aligne le classifieur sur ce motif.
/// </summary>
public static class BottleneckReasonKeys
{
    public const string NoMeasurement = "Bottleneck.Reason.NoMeasurement";
    public const string GpuBusyUnmeasured = "Bottleneck.Reason.GpuBusyUnmeasured";
    public const string CommitPressure = "Bottleneck.Reason.CommitPressure";
    public const string Capped = "Bottleneck.Reason.Capped";
    public const string Throttling = "Bottleneck.Reason.Throttling";
    public const string FramePacing = "Bottleneck.Reason.FramePacing";
    public const string GpuBound = "Bottleneck.Reason.GpuBound";
    public const string CpuBoundStrong = "Bottleneck.Reason.CpuBoundStrong";
    public const string CpuOrAppBound = "Bottleneck.Reason.CpuOrAppBound";
    public const string MixedGpuLeaning = "Bottleneck.Reason.MixedGpuLeaning";
    public const string LatencyOnly = "Bottleneck.Reason.LatencyOnly";
    public const string BetweenStates = "Bottleneck.Reason.BetweenStates";
}

/// <param name="Reason">
/// Le motif technique, en anglais, pour le journal et le support. Il n'atteint pas le client :
/// les bords rendent <paramref name="ReasonKey"/> dans leur langue.
/// </param>
/// <param name="ReasonKey">La clé du motif : voir <see cref="BottleneckReasonKeys"/>.</param>
/// <param name="ReasonValues">
/// Les grandeurs que la phrase interpole, dans l'ordre où le bord les attend. Vide quand la phrase
/// n'en porte aucune. Des nombres, jamais du texte déjà formaté : le formatage appartient au bord,
/// parce que « 96 % » et « 96,0 % » ne s'écrivent pas pareil selon la langue.
/// </param>
public readonly record struct BottleneckVerdict(
    BottleneckState State,
    BottleneckConfidence Confidence,
    string Reason,
    bool Provisional,
    string? ReasonKey = null,
    IReadOnlyList<double>? ReasonValues = null)
{
    /// <summary>Vrai quand le verdict autorise une affirmation de gain mesuré. Faux en
    /// <see cref="BottleneckState.UnknownPartial"/>, en provisoire, et sur une session bridée
    /// par un cap : dans les trois cas la mesure ne peut rien prouver, pour trois raisons
    /// différentes.</summary>
    public bool AllowsMeasuredGainClaim =>
        !Provisional
        && State != BottleneckState.UnknownPartial
        && State != BottleneckState.CappedOrVsyncLimited;
}
