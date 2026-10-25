using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : GpuBench, une enveloppe de mesure sur le runner de calcul déterministe.
// Transforme une passe en résultat répétable et comparable pour l'A/B de tools/r2bench :
// un score de débit + un verdict de stabilité + un drapeau « somme de contrôle juste ».
//
// Le score (ElementIterationsPerSecond = ElementCount * IterationsCompleted / secondes) est
// le nombre comparable : la charge étant identique d'une passe à l'autre, seuls l'horloge et
// le débit du GPU le font bouger. Donc : passe à l'origine, pose du +décalage par le helper
// (étape UAC à part), nouvelle passe, comparaison nette des scores.
//
// Correspondance des verdicts, même contrat de sécurité que la sonde :
//   * sain + somme de contrôle juste → Stable
//   * DeviceLost ou somme de contrôle fausse → Unstable  (« pas de TDR ≠ stable »)
//   * erreur / panne du runner → Inconclusive
// Ne lève jamais. Lecture et calcul seulement : pas de NVAPI, pas d'écriture GPU, pas d'élévation.
// -----------------------------------------------------------------------

/// <summary>Un résultat de banc répétable : un score de débit comparable, le verdict de
/// stabilité, et si la somme de contrôle déterministe correspond à la réponse connue.</summary>
public sealed record GpuBenchResult(
    StabilityOutcome Stability,
    bool ChecksumOk,
    ulong Checksum,
    int IterationsCompleted,
    double ElapsedMilliseconds,
    double ElementIterationsPerSecond);

public sealed class GpuBench
{
    private readonly IGpuComputeRunner _runner;
    private readonly GpuWorkloadSpec _spec;

    public GpuBench(IGpuComputeRunner runner) : this(runner, GpuBenchWorkload.Default) { }

    public GpuBench(IGpuComputeRunner runner, GpuWorkloadSpec spec)
        => (_runner, _spec) = (runner, spec);

    public GpuWorkloadSpec Spec => _spec;

    /// <param name="gpuIndex">La carte réglée. Le banc charge la seule carte dédiée de la machine ; avec plusieurs, rien ne relie
    /// cet index à un adaptateur DXGI et le runner refuse de juger (non concluant, jamais stable).</param>
    public Result<GpuBenchResult> Run(int gpuIndex, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var run = _runner.Run(_spec, duration, ct);
            if (!run.IsOk || run.Value is null)
                return Result<GpuBenchResult>.Ok(Inconclusive());

            var o = run.Value;
            switch (o.DeviceStatus)
            {
                case GpuDeviceStatus.DeviceLost:
                    return Result<GpuBenchResult>.Ok(
                        new GpuBenchResult(StabilityOutcome.Unstable, ChecksumOk: false,
                            o.Checksum, o.IterationsCompleted, o.ElapsedMilliseconds, Score(o)));

                case GpuDeviceStatus.Healthy:
                    var ok = o.Checksum == GpuBenchWorkload.ComputeKnownChecksum(_spec);
                    return Result<GpuBenchResult>.Ok(
                        new GpuBenchResult(
                            ok ? StabilityOutcome.Stable : StabilityOutcome.Unstable,
                            ChecksumOk: ok,
                            o.Checksum, o.IterationsCompleted, o.ElapsedMilliseconds, Score(o)));

                default: // Error
                    return Result<GpuBenchResult>.Ok(Inconclusive());
            }
        }
        catch (Exception)
        {
            return Result<GpuBenchResult>.Ok(Inconclusive());
        }
    }

    private double Score(GpuComputeOutcome o)
    {
        var seconds = o.ElapsedMilliseconds / 1000.0;
        if (seconds <= 0) return 0;
        return (double)_spec.ElementCount * o.IterationsCompleted / seconds;
    }

    private static GpuBenchResult Inconclusive()
        => new(StabilityOutcome.Inconclusive, ChecksumOk: false, 0, 0, 0, 0);
}
