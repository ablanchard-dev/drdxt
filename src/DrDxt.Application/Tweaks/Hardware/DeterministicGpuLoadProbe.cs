using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : le vrai IGpuLoadProbe, pure orchestration sur la fine couture D3D11
// IGpuComputeRunner. Il lance une charge de calcul GPU bornée et déterministe et traduit le
// résultat en GpuLoadResult, sûr par construction :
//
//   * périphérique sain + somme de contrôle == réponse connue → CompletedDeviceHealthy
//   * DeviceLost (TDR)                                       → DeviceLost
//   * périphérique sain mais somme de contrôle fausse         → DeviceLost  (corruption
//                                                                silencieuse ; selon la recherche « pas de TDR ≠ stable »)
//   * Error / Fail / exception du runner                     → CouldNotRun (→ Inconclusive → retour)
//
// Ne lève jamais à travers la frontière IGpuLoadProbe. La réponse connue se calcule à partir
// de la même spec que celle donnée au runner : le contrôle est un vrai test à réponse connue.
// La seule surface non testable unitairement est le vrai IGpuComputeRunner D3D11 (smoke test réel).
// -----------------------------------------------------------------------
public sealed class DeterministicGpuLoadProbe : IGpuLoadProbe
{
    private readonly IGpuComputeRunner _runner;
    private readonly GpuWorkloadSpec _spec;

    public DeterministicGpuLoadProbe(IGpuComputeRunner runner) : this(runner, GpuBenchWorkload.Default) { }

    public DeterministicGpuLoadProbe(IGpuComputeRunner runner, GpuWorkloadSpec spec)
        => (_runner, _spec) = (runner, spec);

    public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var run = _runner.Run(_spec, duration, ct);
            if (!run.IsOk || run.Value is null)
                return Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));

            var outcome = run.Value;
            var result = outcome.DeviceStatus switch
            {
                GpuDeviceStatus.DeviceLost => GpuLoadResult.DeviceLost,
                GpuDeviceStatus.Error => GpuLoadResult.CouldNotRun,
                GpuDeviceStatus.Healthy =>
                    outcome.Checksum == GpuBenchWorkload.ComputeKnownChecksum(_spec)
                        ? GpuLoadResult.CompletedDeviceHealthy
                        : GpuLoadResult.DeviceLost,   // corruption silencieuse ⇒ instable
                _ => GpuLoadResult.CouldNotRun,
            };
            return Task.FromResult(Result<GpuLoadResult>.Ok(result));
        }
        catch (Exception)
        {
            // Sécurité : un runner qui explose (ou une annulation) ne doit jamais se lire comme une
            // passe saine. CouldNotRun → Inconclusive → le levier s'annule.
            return Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));
        }
    }
}
