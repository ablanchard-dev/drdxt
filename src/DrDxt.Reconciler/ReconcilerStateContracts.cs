using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// État modifiable du sidecar d'exécution.
//
// Séparé du manifeste scellé pour que les modifications à chaque tour (coupe-circuits, dernier
// build vu par élément, compteur de série de démarrages) ne cassent jamais la signature du
// manifeste. Ce fichier n'est pas signé (il ne porte aucune autorité privilégiée : il ne peut
// qu'en faire moins au réconciliateur, jamais plus) ; il vit à côté du manifeste sous la même
// racine en écriture admin, pour résister à l'altération au repos.
// -----------------------------------------------------------------------

/// <summary>Per-item runtime state: paused flag (kill-switch), les jetons de reference
/// (build + version de pilote) contre lesquels une derive est classee `WindowsReset`, et
/// le suivi de la derniere passe.
///
/// `ApplyReferenceSeedingStore` pose les deux jetons au commit qui suit l'apply (et ne les re-stampe
/// jamais ensuite), et chaque passe du reconciliateur les rearme sur ce qu'elle voit.</summary>
public sealed record ReconcilerItemState(
    string TweakId,
    bool Paused,
    string LastSeenBuild,
    string LastSeenDriverVersion,
    DateTimeOffset LastReconciledAt,
    string LastResultCode);

/// <summary>Tout le sidecar d'exécution : coupe-circuit global, dernier build de mise à jour de
/// fonctionnalité traité par l'hôte, série de démarrages non propres consécutifs (T27b
/// anti-boucle de démarrage), horodatage du dernier marqueur de session propre, et état par élément.</summary>
public sealed record ReconcilerState(
    bool GlobalPaused,
    string LastFeatureUpdateBuild,
    int UncleanBootStreak,
    DateTimeOffset? LastStableSessionAt,
    IReadOnlyList<ReconcilerItemState> Items)
{
    /// <summary>Un défaut vide et sain (première installation / pas de fichier) : rien en pause,
    /// aucun élément, aucun échec de démarrage.</summary>
    public static ReconcilerState Default => new(
        GlobalPaused: false,
        LastFeatureUpdateBuild: string.Empty,
        UncleanBootStreak: 0,
        LastStableSessionAt: null,
        Items: Array.Empty<ReconcilerItemState>());
}

/// <summary>Enregistre le sidecar d'exécution. Fichier absent ⇒ Ok(ReconcilerState.Default).
/// Échec d'E/S → Fail(ErrorCodes.IoFailure).</summary>
public interface IReconcilerStateStore
{
    Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct);
    Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct);
}
