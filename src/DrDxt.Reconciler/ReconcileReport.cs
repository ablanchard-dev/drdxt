using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// L'issue structurée d'une passe de réconciliation / de détection de dérive.
//
// Rendue à côté du Result pour que l'hôte journalise / mesure exactement ce qui a dérivé, ce qui
// a été réappliqué, notifié ou a échoué, sans analyser de chaînes.
// -----------------------------------------------------------------------

/// <summary>Ce que le réconciliateur a fait d'un élément du manifeste pendant cette passe.</summary>
public enum ItemOutcome
{
    /// <summary>Déjà à la valeur voulue : aucune écriture (idempotence, I1).</summary>
    NoOp = 0,
    /// <summary>A dérivé, puis a été réappliqué avec succès et confirmé.</summary>
    Reapplied = 1,
    /// <summary>A dérivé mais classé UserIntentional/Unknown → notifié, pas écrasé (I5).</summary>
    Notified = 2,
    /// <summary>Élément en pause (coupe-circuit) → comparé / journalisé mais pas réappliqué (I7).</summary>
    Skipped = 3,
    /// <summary>La réapplication (ou sa relecture de confirmation) a échoué → élément KO (I2).</summary>
    Failed = 4,
    /// <summary>Refusé : hors allowlist / surface protégée (I3, sécurité).</summary>
    Refused = 5,
    /// <summary>Suspendu par la garde anti-boucle de démarrage (T27b) : un réglage GPU n'a pas été
    /// réappliqué parce que la machine est soupçonnée d'être dans une boucle de démarrage.</summary>
    Suspended = 6,
}

/// <summary>Une ligne du rapport de réconciliation.</summary>
public sealed record ReconcileItemResult(
    string TweakId,
    ItemOutcome Outcome,
    DriftCause? Cause,
    string? CurrentValueJson,
    Error? Error);

/// <summary>Toute la passe. <see cref="GloballyPaused"/> vaut true quand la pause du maintien
/// (<see cref="PauseDuMaintien"/>) a court-circuité la passe.</summary>
public sealed record ReconcileReport(
    bool GloballyPaused,
    IReadOnlyList<ReconcileItemResult> Items)
{
    public IEnumerable<ReconcileItemResult> Reapplied => Items.Where(i => i.Outcome == ItemOutcome.Reapplied);
    public IEnumerable<ReconcileItemResult> Failed => Items.Where(i => i.Outcome == ItemOutcome.Failed);
    public IEnumerable<ReconcileItemResult> Notified => Items.Where(i => i.Outcome == ItemOutcome.Notified);
    public IEnumerable<ReconcileItemResult> Suspended => Items.Where(i => i.Outcome == ItemOutcome.Suspended);
}

