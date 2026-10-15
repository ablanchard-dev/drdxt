using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// OcSweep : la logique de décision pure du balayage ascendant de décalage cœur. Mesure
// l'origine, puis chaque décalage candidat sur le perf-mètre déterministe ; garde le meilleur
// gain prouvé (GpuBenchAb : le delta franchit la marge combinée et c'est stable) ; s'arrête
// au premier pas instable ou raté ; finit toujours en appliquant le meilleur décalage (0 =
// origine quand rien n'a prouvé mieux). Pure orchestration sur deux délégués, sans carte.
// -----------------------------------------------------------------------
public sealed class OcSweepTests
{
    private static GpuBenchAggregate Agg(double mean, double margin = 1, bool stable = true)
        => new(mean, margin, 3, stable);

    // délégué de mesure indexé par le dernier décalage appliqué.
    private static (Func<Result<GpuBenchAggregate>> Measure, Func<int, bool> Apply, List<int> Applied)
        Rig(Dictionary<int, Result<GpuBenchAggregate>> byOffset, Func<int, bool>? applyOverride = null)
    {
        var applied = new List<int>();
        var current = 0;
        bool Apply(int mhz)
        {
            if (applyOverride is not null && !applyOverride(mhz)) return false;
            current = mhz;
            applied.Add(mhz);
            return true;
        }
        Result<GpuBenchAggregate> Measure() =>
            byOffset.TryGetValue(current, out var r)
                ? r
                : Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail, $"no rig entry for {current}");
        return (Measure, Apply, applied);
    }

    [Fact]
    public void Keeps_the_highest_proven_gain_and_ends_applied_at_it()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(104)),   // gain (marge 1+1=2)
            [60] = Result<GpuBenchAggregate>.Ok(Agg(108)),   // gain plus grand
            [90] = Result<GpuBenchAggregate>.Ok(Agg(108.5)), // dans le bruit face à l'origine ? 8.5 > 2 → toujours un gain, meilleur score
        });

        var r = OcSweep.Run(measure, apply, new[] { 30, 60, 90 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(90);
        r.Value.GainPct.Should().BeApproximately(8.5, 0.01);
        applied.Last().Should().Be(90); // finit appliqué au meilleur
        r.Value.EndedAtBest.Should().BeTrue();
    }

    [Fact]
    public void Unstable_candidate_stops_the_sweep_and_reverts_to_the_previous_best()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(105)),                  // gain
            [60] = Result<GpuBenchAggregate>.Ok(Agg(120, stable: false)),  // TDR ou somme de contrôle → stop
        });

        var r = OcSweep.Run(measure, apply, new[] { 30, 60, 90 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(30);
        applied.Last().Should().Be(30);              // revenu au meilleur prouvé
        applied.Should().NotContain(90);             // n'a jamais dépassé l'instabilité
    }

    [Fact]
    public void No_proven_gain_ends_back_at_stock()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100, margin: 5)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(103, margin: 5)),  // dans le bruit combiné
        });

        var r = OcSweep.Run(measure, apply, new[] { 30 });

        r.Value!.BestOffsetMhz.Should().Be(0);
        r.Value.GainPct.Should().Be(0);
        applied.Last().Should().Be(0);
    }

    [Fact]
    public void Out_of_band_candidates_are_never_applied()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 150, 500 }); // > GpuSafeBounds.MaxCoreOffsetMhz (100)

        r.IsOk.Should().BeTrue();
        applied.Should().OnlyContain(o => o == 0);   // juste l'origine initiale et la restauration finale
        r.Value!.BestOffsetMhz.Should().Be(0);
    }

    [Fact]
    public void Memory_band_candidates_pass_when_the_memory_band_check_is_supplied()
    {
        // +300 est hors de la bande cœur (plafond +100) mais dans la bande mémoire (±500) :
        // avec le contrôle de bande mémoire fourni, il doit être balayé, pas écarté en silence.
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [300] = Result<GpuBenchAggregate>.Ok(Agg(105)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 300 },
            isInBand: DrDxt.Application.Tweaks.Gpu.GpuSafeBounds.IsMemoryOffsetSafe);

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(300);
        applied.Should().Contain(300);
    }

    [Fact]
    public void Unstable_stock_fails_clean_without_applying_any_offset()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100, stable: false)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 30 });

        r.IsOk.Should().BeFalse();
        applied.Should().OnlyContain(o => o == 0);
    }

    [Fact]
    public void Failed_apply_stops_and_restores_the_previous_best()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(105)),
        }, applyOverride: mhz => mhz != 60); // l'écriture +60 échoue

        var r = OcSweep.Run(measure, apply, new[] { 30, 60 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(30);
        applied.Last().Should().Be(30);
    }
}
