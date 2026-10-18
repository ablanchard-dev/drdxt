using DrDxt.Core;
using DrDxt.Measurement;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests;

public class BenchmarkRunnerTests
{
    private static IPresentMonSession SessionReturning(params double[] ft)
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(new FrameCapture(ft.ToList(), Array.Empty<double>(), Array.Empty<double>())));
        return s;
    }

    // Rend une capture différente par passe (passe 1, passe 2, …) pour que les agrégats par passe diffèrent.
    private static IPresentMonSession SessionReturningSequence(params IReadOnlyList<double>[] runs)
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        var results = runs.Select(r => Result<FrameCapture>.Ok(new FrameCapture(r, Array.Empty<double>(), Array.Empty<double>()))).ToArray();
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(results[0], results.Skip(1).ToArray());
        return s;
    }

    [Fact]
    public async Task Computes_avg_and_lows_from_frametimes()
    {
        var ft = Enumerable.Repeat(10.0, 100).ToArray(); // 100 fps constants
        var runner = new BenchmarkRunner(SessionReturning(ft));
        var r = await runner.RunAsync(1234, TimeSpan.FromMilliseconds(50), 1, default);
        r.IsOk.Should().BeTrue();
        r.Value!.AvgFps.Should().BeApproximately(100, 0.5);
        r.Value.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Aggregates_frametimes_across_multiple_runs()
    {
        var runner = new BenchmarkRunner(SessionReturning(Enumerable.Repeat(10.0, 50).ToArray()));
        var r = await runner.RunAsync(1234, TimeSpan.FromMilliseconds(50), 5, default);
        r.Value!.Runs.Should().Be(5);
        r.Value.FrameTimesMs.Should().HaveCount(250); // 5 × 50
    }

    [Fact]
    public async Task Margin_is_unreliable_infinite_below_three_runs()
    {
        // 2 passes → la variance entre passes ne peut pas être bornée → marge « non fiable » (∞), donc le
        // verdict A/B ne peut jamais annoncer un gain mesurable sur trop peu de passes.
        var runner = new BenchmarkRunner(SessionReturning(Enumerable.Repeat(10.0, 50).ToArray()));
        var r = await runner.RunAsync(1, TimeSpan.FromMilliseconds(10), 2, default);
        r.IsOk.Should().BeTrue();
        r.Value!.MarginOfError.Should().Be(double.PositiveInfinity);
    }

    [Fact]
    public async Task Margin_comes_from_inter_run_variance_not_frame_count()
    {
        // 3 passes à moyennes différentes (100 / 50 / 66,7) → une marge finie et positive
        // pilotée par l'écart entre passes, quel que soit le nombre d'images par passe.
        var run1 = Enumerable.Repeat(10.0, 300).ToList(); // 100 fps
        var run2 = Enumerable.Repeat(20.0, 300).ToList(); // 50 fps
        var run3 = Enumerable.Repeat(15.0, 300).ToList(); // ~66.7 fps
        var runner = new BenchmarkRunner(SessionReturningSequence(run1, run2, run3));
        var r = await runner.RunAsync(1, TimeSpan.FromMilliseconds(10), 3, default);
        r.Value!.MarginOfError.Should().BeGreaterThan(0);
        double.IsFinite(r.Value.MarginOfError).Should().BeTrue();
    }

    // Le 0,1 % low a sa marge, tirée de sa dispersion entre passes comme celle du 1 % low.
    [Fact]
    public async Task Le_0_1_pourcent_low_a_sa_propre_marge_entre_passes()
    {
        var run1 = Enumerable.Repeat(10.0, 300).ToList();
        var run2 = Enumerable.Repeat(20.0, 300).ToList();
        var run3 = Enumerable.Repeat(15.0, 300).ToList();
        var r = await new BenchmarkRunner(SessionReturningSequence(run1, run2, run3)).RunAsync(1, TimeSpan.FromMilliseconds(10), 3, default);
        r.Value!.P01LowMarginOfError.Should().BeGreaterThan(0);
        double.IsFinite(r.Value.P01LowMarginOfError).Should().BeTrue();

        var une = await new BenchmarkRunner(SessionReturningSequence(run1)).RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        une.Value!.P01LowMarginOfError.Should().Be(double.PositiveInfinity, "sous trois passes, pas de marge inventée");
    }

    [Fact]
    public async Task Identical_runs_have_zero_inter_run_margin()
    {
        var run = Enumerable.Repeat(10.0, 300).ToList();
        var runner = new BenchmarkRunner(SessionReturningSequence(run, run, run));
        var r = await runner.RunAsync(1, TimeSpan.FromMilliseconds(10), 3, default);
        r.Value!.MarginOfError.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public async Task Estimated_latency_is_the_mean_render_present_latency()
    {
        var cap = new FrameCapture(new[] { 10.0, 10.0 }, new[] { 4.0, 6.0 }, Array.Empty<double>());
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(cap));
        var r = await new BenchmarkRunner(s).RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        r.Value!.EstimatedPcLatencyMs.Should().BeApproximately(5.0, 1e-9);
    }

    // Les modes de présentation capturés deviennent la part d'images composées ; la latence PC sans marqueurs (NaN) reste null.
    [Fact]
    public async Task Present_modes_become_the_composed_share_and_nan_pc_latency_stays_null()
    {
        var cap = new FrameCapture(new[] { 10.0, 10.0, 10.0, 10.0 }, Array.Empty<double>(), Array.Empty<double>(),
            PresentModes: new[] { 3, 4, 3, 3 }, PcLatencyMs: new[] { double.NaN, double.NaN, double.NaN, double.NaN });
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(cap));
        var r = await new BenchmarkRunner(s).RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        r.Value!.PartImagesComposees.Should().Be(0.25);
        r.Value.PcLatencyMsMedian.Should().BeNull();
    }

    [Fact]
    public async Task Estimated_latency_is_null_when_no_latency_samples()
    {
        // Temps d'image mais pas de série de latence → « non mesuré » (null), jamais un 0 fabriqué.
        var runner = new BenchmarkRunner(SessionReturning(Enumerable.Repeat(10.0, 50).ToArray()));
        var r = await runner.RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        r.Value!.EstimatedPcLatencyMs.Should().BeNull();
    }

    [Fact]
    public async Task Mean_gpu_busy_is_aggregated_from_the_capture()
    {
        var cap = new FrameCapture(new[] { 10.0, 10.0 }, Array.Empty<double>(), new[] { 6.0, 8.0 });
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(cap));
        var r = await new BenchmarkRunner(s).RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        r.Value!.MeanGpuBusyMs.Should().BeApproximately(7.0, 1e-9);
    }

    [Fact]
    public async Task Mean_gpu_busy_is_null_when_no_samples()
    {
        // Temps d'image mais pas de série d'occupation GPU → « non mesuré » (null), jamais un 0 fabriqué.
        var runner = new BenchmarkRunner(SessionReturning(Enumerable.Repeat(10.0, 50).ToArray()));
        var r = await runner.RunAsync(1, TimeSpan.FromMilliseconds(10), 1, default);
        r.Value!.MeanGpuBusyMs.Should().BeNull();
    }

    [Fact]
    public async Task Capture_failure_propagates_as_fail()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Fail(ErrorCodes.NoFrames, "x"));
        // Sans cette réponse fixée, le test demandait à la vraie machine si le PID 1234 vivait.
        // Sur un runner où il vivait, le code devenait NoPresentingFrames : rouge en CI deux fois ce jour-là.
        var r = await new BenchmarkRunner(s, isProcessAlive: _ => false).RunAsync(1234, TimeSpan.FromMilliseconds(10), 3, default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NoFrames);
    }

    [Fact]
    public async Task Service_unavailable_fails_with_pmon_unavailable()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(false));
        var r = await new BenchmarkRunner(s).RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.PmonUnavailable);
    }

    [Fact]
    public async Task Invalid_run_count_fails()
    {
        var r = await new BenchmarkRunner(SessionReturning(10.0)).RunAsync(1234, TimeSpan.FromSeconds(1), 0, default);
        r.IsOk.Should().BeFalse();
    }

    [Fact]
    public async Task Live_process_but_zero_frames_is_reported_as_anticheat_block()
    {
        const int livePid = 4321;
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(livePid, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Fail(ErrorCodes.NoFrames, "0"));

        // la sonde de processus dit que la cible est vivante
        var runner = new BenchmarkRunner(s, isProcessAlive: _ => true);
        var r = await runner.RunAsync(livePid, TimeSpan.FromSeconds(1), 1, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NoPresentingFrames);
    }

    [Fact]
    public async Task Dead_process_with_zero_frames_stays_no_frames()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Fail(ErrorCodes.NoFrames, "0"));

        var runner = new BenchmarkRunner(s, isProcessAlive: _ => false);
        var r = await runner.RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.NoFrames);
    }

    [Fact]
    public async Task Live_process_capture_fail_is_not_remapped()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, "x"));

        var runner = new BenchmarkRunner(s, isProcessAlive: _ => true);
        var r = await runner.RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        // seul NO_FRAMES est retraduit ; les autres échecs passent intacts
        r.Error!.Value.Code.Should().Be(ErrorCodes.CaptureFail);
    }
}
