using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// OcSweep : balayage croissant du décalage cœur sur le compteur de performance déterministe.
//   1. poser 0 (origine) et mesurer ; une origine instable fait échouer tout le balayage, rien n'est posé ;
//   2. chaque candidat, croissant, dans la plage de GpuSafeBounds (les autres écartés : jamais d'écriture hors règle) :
//      pose → mesure → verdict GpuBenchAb contre l'origine ;
//   3. garder le meilleur gain prouvé (écart au-delà de la marge cumulée, passes stables) ; la première étape instable
//      ou ratée arrête ;
//   4. finir toujours au meilleur décalage prouvé (0 sinon), même en sortie anticipée.
// Orchestration pure sur deux délégués (mesurer / poser), testée sans GPU ; le banc octune les branche sur
// GpuBenchSession et la vraie écriture NVAPI.

/// <summary>Une étape du balayage : le décalage essayé, si l'écriture a pris, l'agrégat mesuré
/// (null si la pose ou la mesure a échoué) et son verdict contre l'origine.</summary>
public sealed record OcSweepStep(int OffsetMhz, bool ApplyOk, GpuBenchAggregate? Aggregate, GpuBenchAbVerdict? Verdict);

/// <summary>Résultat du balayage : le meilleur décalage prouvé (0 = origine), son gain en %
/// contre l'origine (0 si origine), l'agrégat d'origine, chaque étape, et si l'écriture finale du
/// meilleur a réussi (faux = le GPU n'est peut-être pas à BestOffsetMhz : le dire fort).</summary>
public sealed record OcSweepOutcome(
    int BestOffsetMhz,
    double GainPct,
    GpuBenchAggregate Stock,
    IReadOnlyList<OcSweepStep> Steps,
    bool EndedAtBest);

public static class OcSweep
{
    public static Result<OcSweepOutcome> Run(
        Func<Result<GpuBenchAggregate>> measure,
        Func<int, bool> applyOffset,
        IReadOnlyList<int> candidateOffsetsMhz,
        // Le domaine d'horloge décide de la plage sûre : le cœur par défaut ; passer
        // GpuSafeBounds.IsMemoryOffsetSafe pour un balayage du décalage mémoire (±500).
        Func<int, bool>? isInBand = null)
    {
        isInBand ??= GpuSafeBounds.IsCoreOffsetSafe;

        // Dans la plage, hors origine, croissant, sans doublon. Les candidats hors plage sont
        // écartés ici : une erreur d'appelant n'atteint jamais le GPU (défense avant l'écriture).
        var candidates = candidateOffsetsMhz
            .Where(o => o != 0 && isInBand(o))
            .Distinct()
            .OrderBy(o => o)
            .ToList();

        if (!applyOffset(0))
            return Result<OcSweepOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not set the stock offset.");

        var stockRead = measure();
        if (!stockRead.IsOk || stockRead.Value is null)
            return Result<OcSweepOutcome>.Fail(stockRead.Error?.Code ?? ErrorCodes.CaptureFail,
                $"Stock measurement failed: {stockRead.Error?.Message}");
        var stock = stockRead.Value;
        if (!stock.AllStable)
            return Result<OcSweepOutcome>.Fail(ErrorCodes.CaptureFail,
                "Stock run is not stable: the machine is not measurable; no offset was applied.");

        var steps = new List<OcSweepStep>(candidates.Count);
        var bestOffset = 0;
        var bestScore = stock.MeanScore;

        foreach (var offset in candidates)
        {
            if (!applyOffset(offset))
            {
                steps.Add(new OcSweepStep(offset, ApplyOk: false, null, null));
                break; // une écriture refusée arrête le balayage ; remise plus bas
            }

            var read = measure();
            if (!read.IsOk || read.Value is null)
            {
                steps.Add(new OcSweepStep(offset, ApplyOk: true, null, null));
                break;
            }

            var verdict = GpuBenchAb.Compare(stock, read.Value);
            steps.Add(new OcSweepStep(offset, ApplyOk: true, read.Value, verdict));

            if (verdict == GpuBenchAbVerdict.Unstable)
                break; // jamais au-delà de la première instabilité

            if (verdict == GpuBenchAbVerdict.Gain && read.Value.MeanScore > bestScore)
            {
                bestOffset = offset;
                bestScore = read.Value.MeanScore;
            }
        }

        // Sortie chirurgicale : le GPU finit toujours au meilleur décalage prouvé (l'origine sinon).
        var endedAtBest = applyOffset(bestOffset);
        var gainPct = bestOffset == 0 ? 0 : (bestScore - stock.MeanScore) / stock.MeanScore * 100.0;

        return Result<OcSweepOutcome>.Ok(new OcSweepOutcome(bestOffset, gainPct, stock, steps, endedAtBest));
    }
}
