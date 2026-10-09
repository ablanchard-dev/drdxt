using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Mutable runtime sidecar state (plan §5).
//
// Kept SEPARATE from the sealed manifest so per-tick mutation (kill-switches, last
// seen build per item, boot-streak counter) never breaks the manifest's signature.
// This file is not signed (it carries no privileged authority — it can only make
// the reconciler do LESS, never more); it lives next to the manifest under the same
// admin-write root for tamper-resistance at rest.
// -----------------------------------------------------------------------

/// <summary>Per-item runtime state: paused flag (kill-switch), les jetons de reference
/// (build + version de pilote) contre lesquels une derive est classee `WindowsReset`, et
/// le suivi de la derniere passe.
///
/// Corrige LE 11/09/2026 — cette prose disait « the OS build seen at last apply ». C'ETAIT
/// Faux : les deux jetons n'etaient ecrits que par `PersistBookkeepingAsync`, donc a la fin
/// d'une passe du reconciliateur, jamais a l'apply. Depuis, `ApplyReferenceSeedingStore` les
/// Pose vraiment au commit qui suit l'apply (et ne les re-stampe jamais ensuite), et la passe
/// les rearme sur ce qu'elle voit. La prose dit maintenant ce que le code fait.</summary>
public sealed record ReconcilerItemState(
    string TweakId,
    bool Paused,
    string LastSeenBuild,
    string LastSeenDriverVersion,
    DateTimeOffset LastReconciledAt,
    string LastResultCode);

/// <summary>The whole runtime sidecar: global kill-switch, the last feature-update
/// build the host acted on, the consecutive unclean-boot streak (T27b anti
/// boot-loop), the timestamp of the last clean session marker, and per-item state.</summary>
public sealed record ReconcilerState(
    bool GlobalPaused,
    string LastFeatureUpdateBuild,
    int UncleanBootStreak,
    DateTimeOffset? LastStableSessionAt,
    IReadOnlyList<ReconcilerItemState> Items)
{
    /// <summary>A sane empty default (first install / no file): nothing paused,
    /// no items, no boot failures.</summary>
    public static ReconcilerState Default => new(
        GlobalPaused: false,
        LastFeatureUpdateBuild: string.Empty,
        UncleanBootStreak: 0,
        LastStableSessionAt: null,
        Items: Array.Empty<ReconcilerItemState>());
}

/// <summary>Persists the runtime sidecar. Absent file ⇒ Ok(ReconcilerState.Default)
/// (plan T14). IO failure → Fail(ErrorCodes.IoFailure).</summary>
public interface IReconcilerStateStore
{
    Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct);
    Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct);
}
