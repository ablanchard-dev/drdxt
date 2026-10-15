using DrDxt.Reconciler.Reseau;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

/// <summary>
/// La protection du ping, portée par le service de démarrage. Retire un plafond resté au démarrage,
/// bat à intervalle régulier (<see cref="ProtectionDuPingParLeService.TickAsync"/>), retire tout à l'arrêt.
/// Ne fait rien tant que le joueur n'a rien retenu : la décision est dans la classe testée, ce fichier ne fait
/// que la cadencer.
/// </summary>
public sealed class ProtectionDuPingWorker : BackgroundService
{
    public static readonly TimeSpan BattementParDefaut = TimeSpan.FromSeconds(5);

    private readonly ProtectionDuPingParLeService _protection;
    private readonly TimeSpan _battement;

    public ProtectionDuPingWorker(ProtectionDuPingParLeService protection, TimeSpan battement)
        => (_protection, _battement) = (protection, battement);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _protection.DemarrerAsync(stoppingToken).ConfigureAwait(false);
            using var minuterie = new PeriodicTimer(_battement);
            do { await _protection.TickAsync(stoppingToken).ConfigureAwait(false); }
            while (await minuterie.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt du service : le retrait se fait dans StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _protection.ArreterAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
