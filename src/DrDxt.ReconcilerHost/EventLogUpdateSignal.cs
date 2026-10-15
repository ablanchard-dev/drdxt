using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// EventLogUpdateSignal : l'IUpdateSignal de production.
//
// La valeur de confirmation vient d'une installation Windows Update récente et réussie dans le
// journal d'événements (WindowsUpdateClient, lu par `WindowsUpdateEventLog`, le même lecteur que
// l'audit de santé). Le détecteur de build principal reste l'autorité ; ce signal ne fait
// qu'ajouter un déclenchement plus tôt, et un échec de lecture est un `Fail`, que le déclencheur
// traite comme « pas de signal en plus » (FeatureUpdateTrigger). Ne lève jamais.
//
// Les mises à jour de définitions de Defender sont exclues. Windows Update les installe plusieurs
// fois par jour et elles ne touchent aucun réglage que tient DrDXT ; les compter ferait passer
// chaque jour pour une mise à jour. « Defender » est un nom de marque, identique dans toutes les
// langues de Windows.
// -----------------------------------------------------------------------
public sealed class EventLogUpdateSignal : IUpdateSignal
{
    internal static readonly TimeSpan Fenetre = TimeSpan.FromHours(24);

    private readonly IWindowsUpdateLog _journal;
    private readonly Func<DateTimeOffset> _maintenant;

    public EventLogUpdateSignal() : this(new WindowsUpdateEventLog(), () => DateTimeOffset.UtcNow) { }

    internal EventLogUpdateSignal(IWindowsUpdateLog journal, Func<DateTimeOffset> maintenant)
        => (_journal, _maintenant) = (journal, maintenant);

    public Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct)
    {
        try
        {
            var lu = _journal.Depuis(_maintenant() - Fenetre);
            if (!lu.IsOk)
                return Task.FromResult(Result<bool>.Fail(lu.Error!.Value.Code, lu.Error.Value.Message));
            var recente = lu.Value!.Any(e => e.Reussi
                && !e.Titre.Contains("Defender", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Result<bool>.Ok(recente));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Fail(ErrorCodes.DetectionFailed, ex.Message));
        }
    }
}
