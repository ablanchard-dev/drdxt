using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// GpuStabilityTester : transforme une brève sonde de charge GPU en verdict de stabilité pour
// le levier de sous-tension. Critique pour la sécurité, sûr par construction :
//   * Stable      seulement si la charge s'est terminée avec un périphérique sain,
//   * Unstable    si le périphérique a été perdu (TDR) pendant la charge,
//   * Inconclusive pour tout le reste (panne de la sonde, résultat inconnu, exception) : traité comme « retour ».
// Ne lève jamais. La charge est l'IGpuLoadProbe injecté (D3D11 dans Detection, smoke test) ; l'orchestration est pure.
// -----------------------------------------------------------------------
public sealed class GpuStabilityTester : IStabilityTester
{
    // Quelques secondes suffisent pour qu'un décalage instable tout juste posé provoque un TDR,
    // sans rendre la pose lente. L'écran dit cette durée : elle ne prouve pas la stabilité en jeu.
    public static readonly TimeSpan DureeDeCharge = TimeSpan.FromSeconds(3);

    private readonly IGpuLoadProbe _probe;

    public GpuStabilityTester(IGpuLoadProbe probe) => _probe = probe;

    public async Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct)
    {
        try
        {
            var load = await _probe.RunBriefLoadAsync(gpuIndex, DureeDeCharge, ct).ConfigureAwait(false);
            if (!load.IsOk)
                return Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive);

            return load.Value switch
            {
                GpuLoadResult.CompletedDeviceHealthy => Result<StabilityOutcome>.Ok(StabilityOutcome.Stable),
                GpuLoadResult.DeviceLost => Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable),
                _ => Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive),
            };
        }
        catch (Exception)
        {
            // Sécurité : une sonde qui explose (ou une annulation) ne doit jamais se lire comme
            // Stable. Inconclusive → le levier s'annule.
            return Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive);
        }
    }
}
