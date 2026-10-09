namespace DrDxt.Core;

// --- P2 Safety / rollback seams (M2 Phase B) ---
//
// These are the testable boundaries the SafetyManager depends on so that the
// whole "never apply without a verified restore point" / "revert restores the
// captured original" logic can be unit-tested with fakes — nothing here touches
// the real System Restore or runs anything elevated. The actual privileged work
// (creating the restore point) still flows through IElevatedClient + a typed
// CreateRestorePointOp; this seam only models "is restore usable" + verification.

/// <summary>Abstraction over Windows System Restore, kept testable. Implementations
/// answer whether restore is usable, try to enable it, and verify that a freshly
/// created point actually exists (GPO/SKU can silently no-op the create).</summary>
public interface IRestorePointService
{
    /// <summary>True when System Restore is enabled and a point can be created.</summary>
    bool IsEnabled();

    /// <summary>Best-effort attempt to enable System Restore (may silently fail on
    /// GPO-locked / unsupported SKUs). Returns true only if restore is usable after.</summary>
    bool TryEnable();

    /// <summary>14/09/2026 — la date (UTC) du point de restauration le plus recent cree depuis
    /// <paramref name="depuisUtc"/>, quel que soit son nom, ou <c>null</c> s'il n'y en a aucun — la
    /// garde contre une creation silencieusement ignoree (GPO/SKU).
    /// Pourquoi « quel que soit son nom » : par defaut Windows saute la creation s'il existe un point de
    /// moins de 24 h et repond quand meme « reussi » (Microsoft Learn, SRSetRestorePointW). Ce point-la,
    /// Windows Update ou autre, date d'avant nos changements : il protege, et l'ecran le dit.</summary>
    DateTime? DernierPointDepuis(DateTime depuisUtc);
}

/// <summary>UI-facing consent boundary: ask the user whether to enable System
/// Restore when it is off. Abstracted so the SafetyManager logic is testable
/// without any real prompt.</summary>
public interface IConsentPrompt
{
    bool AskEnableRestore();
}
