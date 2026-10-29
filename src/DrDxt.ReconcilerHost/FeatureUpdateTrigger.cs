using DrDxt.Core;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// IUpdateSignal : un signal de confirmation « une mise à jour / installation Windows vient de se
// terminer », derrière une petite couture pour tester la composition du déclencheur, tandis que
// la vraie lecture du journal d'événements reste un bord mince, testé à part en smoke test.
// -----------------------------------------------------------------------

/// <summary>Lit un signal de mise à jour de confirmation : en général une fin récente de Windows
/// Update / Setup dans le journal d'événements. Confirmation seulement : il peut se déclencher
/// avant que le build change, mais le détecteur de build est l'autorité principale. Illisible →
/// Fail (le déclencheur traite un échec de confirmation au mieux, voir FeatureUpdateTrigger).</summary>
public interface IUpdateSignal
{
    Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct);
}

// FeatureUpdateTrigger : le détecteur de build principal et le signal d'événement de confirmation ; l'un ou l'autre
// déclenche. Le principal fait autorité : sa lecture ratée fait échouer le déclencheur (un build inconnu ne passe pas
// pour « pas de mise à jour »). La confirmation est au mieux : son échec ne masque ni ne fabrique rien, elle ajoute
// seulement un déclenchement précoce quand elle dit « oui ».
public sealed class FeatureUpdateTrigger
{
    private readonly FeatureUpdateDetector _buildDetector;
    private readonly IUpdateSignal _eventSignal;

    public FeatureUpdateTrigger(FeatureUpdateDetector buildDetector, IUpdateSignal eventSignal)
    {
        _buildDetector = buildDetector;
        _eventSignal = eventSignal;
    }

    /// <summary>Vrai si et seulement si une réconciliation doit tourner parce qu'une mise à jour de
    /// fonctionnalité est détectée par le signal de build principal ou confirmée par le signal
    /// d'événement. Échec de lecture principale → Fail.</summary>
    public async Task<Result<bool>> ShouldReconcileAsync(CancellationToken ct)
    {
        // Principal (fait autorité). Un échec ici est un vrai échec, pas « pas de mise à jour ».
        var primary = await _buildDetector.WasFeatureUpdateInstalledAsync(ct);
        if (!primary.IsOk)
            return primary;

        if (primary.Value)
            return Result<bool>.Ok(true);

        // Confirmation (au mieux, en plus). Un journal d'événements illisible ne change pas le principal
        // (false) en échec : on n'a simplement pas de signal en plus.
        var corroborating = await _eventSignal.RecentSetupCompletionAsync(ct);
        if (corroborating.IsOk && corroborating.Value)
            return Result<bool>.Ok(true);

        return Result<bool>.Ok(false);
    }
}
