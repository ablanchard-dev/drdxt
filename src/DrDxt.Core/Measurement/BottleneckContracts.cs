namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Le classifieur de goulot : contrats.
//
// Il relie la mesure aux recommandations : sans lui, une machine GPU-bound à 97 % et une machine étranglée par la
// validation mémoire recevraient les mêmes leviers. La table vient de la note de conception (voir
// `BottleneckClassifier`), dérivée de la sémantique des métriques PresentMon, dont la spec dit :
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
/// Ensemble fermé : les dix états de la table de décision. Écrit par son nom dans les parties gardées : un entier
/// changerait de sens au premier état ajouté avant lui. Un ancien entier se relit encore.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BottleneckState>))]
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
/// Les motifs que le classifieur peut rendre, en clés et non en phrases.
///
/// Une clé et ses valeurs, jamais une phrase construite : chaque bord (rapport, interface, journal) la dit dans sa
/// langue, comme <c>Coverage.Reason.*</c>. Une phrase anglaise construite ici finissait telle quelle dans le rapport
/// français (relevé le 05/09/2026).
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
