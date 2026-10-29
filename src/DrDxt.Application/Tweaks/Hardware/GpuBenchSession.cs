using DrDxt.Core;
using DrDxt.Measurement;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Le compteur de performance GPU déterministe : GpuBench lancé N fois, une moyenne et une marge entre passes, pour une
// vraie barre d'erreur sur l'A/B origine contre décalage. Charge identique : seuls l'horloge et le débit du GPU font
// bouger le score, sans bruit de scène. La marge est celle des frametimes (FrametimeStatistics.InterRunMarginOfError,
// +Infinity sous 3 passes).
// -----------------------------------------------------------------------

/// <summary>Agrégat de N passes déterministes du banc : le score de débit moyen, sa marge à
/// 95 % entre passes (sur les passes stables ; +Infinity sous 3), combien de passes étaient
/// stables, et si toutes l'étaient (somme de contrôle juste, pas de TDR). Un agrégat avec
/// AllStable=false n'est pas fiable pour un A/B : un candidat instable (décalage) doit être annulé.</summary>
public sealed record GpuBenchAggregate(double MeanScore, double MarginOfError, int StableRuns, bool AllStable);

public sealed class GpuBenchSession
{
    private readonly GpuBench _bench;

    public GpuBenchSession(GpuBench bench) => _bench = bench;

    /// <summary>Lance le banc déterministe <paramref name="runs"/> fois (>=1). Garde le score de
    /// débit de chaque passe stable ; toute passe instable ou non concluante met AllStable à faux
    /// et sort du score. La marge exige ≥3 scores stables (sinon +Infinity : aucun gain ne peut
    /// s'affirmer avec moins). N'échoue proprement que si aucune passe n'a pu tourner.</summary>
    public Result<GpuBenchAggregate> Measure(int gpuIndex, TimeSpan perRun, int runs, CancellationToken ct)
    {
        if (runs < 1)
            return Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail, "runs must be >= 1.");

        var scores = new List<double>(runs);
        var allStable = true;

        for (var i = 0; i < runs; i++)
        {
            var r = _bench.Run(gpuIndex, perRun, ct);
            if (!r.IsOk || r.Value is null || r.Value.Stability != StabilityOutcome.Stable)
            {
                allStable = false; // TDR / somme de contrôle fausse / passe impossible : jamais un bon score
                continue;
            }
            scores.Add(r.Value.ElementIterationsPerSecond);
        }

        if (scores.Count == 0)
            return Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail,
                "No stable bench run: GPU unavailable, or the applied setting is unstable.");

        var mean = scores.Average();
        var margin = FrametimeStatistics.InterRunMarginOfError(scores); // +inf sous 3 passes stables
        return Result<GpuBenchAggregate>.Ok(new GpuBenchAggregate(mean, margin, scores.Count, allStable));
    }
}

/// <summary>Le verdict net origine contre candidat d'un A/B de réglage GPU.</summary>
public enum GpuBenchAbVerdict
{
    /// <summary>Le score du candidat bat l'origine au-delà de la marge cumulée entre passes.</summary>
    Gain,
    /// <summary>Le score du candidat est sous l'origine au-delà de la marge cumulée.</summary>
    Regression,
    /// <summary>Le score a bougé dans la marge cumulée : pas un changement prouvable.</summary>
    NoMeasurableChange,
    /// <summary>Un des deux côtés a eu une passe instable : le candidat doit être annulé, jamais gardé.</summary>
    Unstable,
}

/// <summary>Verdict A/B pur entre deux <see cref="GpuBenchAggregate"/>. Un gain exige que l'écart
/// de score dépasse la marge cumulée (jamais une affirmation dans le bruit) et que les deux côtés
/// soient entièrement stables : un candidat instable est toujours <see cref="GpuBenchAbVerdict.Unstable"/> (retour).</summary>
public static class GpuBenchAb
{
    public static GpuBenchAbVerdict Compare(GpuBenchAggregate stock, GpuBenchAggregate candidate)
    {
        if (!stock.AllStable || !candidate.AllStable)
            return GpuBenchAbVerdict.Unstable;

        var combinedMargin = stock.MarginOfError + candidate.MarginOfError;
        var delta = candidate.MeanScore - stock.MeanScore;

        if (delta > combinedMargin) return GpuBenchAbVerdict.Gain;
        if (delta < -combinedMargin) return GpuBenchAbVerdict.Regression;
        return GpuBenchAbVerdict.NoMeasurableChange;
    }
}
