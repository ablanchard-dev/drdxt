using DrDxt.Core;

namespace DrDxt.Reconciler;

// État modifiable du sidecar d'exécution, séparé du manifeste scellé : les modifications de chaque tour (pause par
// élément, dernier build vu, série de démarrages) ne cassent pas sa signature. Non signé, il ne peut qu'en faire moins au
// réconciliateur, jamais plus ; il vit sous la même racine en écriture admin, contre l'altération au repos.

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

/// <summary>Tout le sidecar d'exécution : dernier build de mise à jour de
/// fonctionnalité traité par l'hôte, série de démarrages non propres consécutifs (T27b
/// anti-boucle de démarrage), horodatage du dernier marqueur de session propre, et état par élément.</summary>
public sealed record ReconcilerState(
    string LastFeatureUpdateBuild,
    int UncleanBootStreak,
    DateTimeOffset? LastStableSessionAt,
    IReadOnlyList<ReconcilerItemState> Items)
{
    /// <summary>Un défaut vide et sain (première installation / pas de fichier) : rien en pause,
    /// aucun élément, aucun échec de démarrage.</summary>
    public static ReconcilerState Default => new(
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

    /// <summary>Lire, modifier, réécrire d'un seul tenant : le helper et le service réécrivent le même fichier, et
    /// l'écriture de l'un ne doit pas se perdre entre la lecture et la réécriture de l'autre. <paramref name="modifier"/>
    /// rend null pour ne rien écrire. Rend l'état d'après. Un magasin en mémoire n'a qu'un écrivain : il enchaîne.</summary>
    async Task<Result<ReconcilerState>> MettreAJourAsync(Func<ReconcilerState, ReconcilerState?> modifier, CancellationToken ct)
    {
        var lu = await LoadAsync(ct).ConfigureAwait(false);
        if (!lu.IsOk)
            return lu;
        if (modifier(lu.Value!) is not { } suivant)
            return lu;
        var ecrit = await SaveAsync(suivant, ct).ConfigureAwait(false);
        return ecrit.IsOk ? Result<ReconcilerState>.Ok(suivant) : Result<ReconcilerState>.Fail(ecrit.Error!.Value.Code, ecrit.Error.Value.Message);
    }
}
