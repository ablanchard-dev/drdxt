using DrDxt.Core;
using DrDxt.Measurement.Frames;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Frames;

public class RepliDxgiBenchmarkRunnerTests
{
    private sealed class PresentMon(Result<BenchmarkResult> r) : IBenchmarkRunner
    {
        public int Appels;
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        { Appels++; return Task.FromResult(r); }
    }

    private static BenchmarkResult Serie(double ms, int n = 100)
        => FrametimeStatistics.PasseDepuisSerie(Enumerable.Repeat(ms, n).ToList(), 1, 0);

    [Fact]
    public void Les_intervalles_entre_Present_sont_les_frametimes_et_deux_Present_au_meme_tick_ne_font_pas_une_image()
    {
        // 5 ms = 50 000 ticks de 100 ns ; l'ordre d'arrivee ne compte pas.
        CompteurPresentsDxgi.Intervalles(new long[] { 100_000, 0, 50_000, 100_000, 150_000 })
            .Should().Equal(5.0, 5.0, 5.0);
    }

    // 26/09/2026 : sans `-max`, une session laissee ouverte par un plantage ecrit tous les evenements
    // DXGI du PC dans %TEMP% jusqu'au redemarrage. Borne, et une trace qui touche la borne est tronquee :
    // elle le dit au lieu de rendre une mesure partielle.
    [Fact]
    public void La_trace_est_bornee_et_une_trace_pleine_est_dite_tronquee()
    {
        CompteurPresentsDxgi.ArgumentsCreation(@"C:\t\x.etl").Should().Contain($"-max {CompteurPresentsDxgi.MaxMo}");
        CompteurPresentsDxgi.TracePleine(CompteurPresentsDxgi.MaxMo * 1024L * 1024).Should().BeTrue();
        CompteurPresentsDxgi.TracePleine(5L * 1024 * 1024).Should().BeFalse();
    }

    // 26/09/2026 : un plantage en pleine capture laisse son .etl (jusqu'a la borne) ; son nom change a
    // chaque mesure, personne ne le reprenait. Une seule session a la fois : au demarrage, tout .etl du
    // compteur est un reste. Les autres fichiers du dossier ne sont pas touches.
    [Fact]
    public void Les_traces_restees_d_un_plantage_sont_rangees_et_rien_d_autre()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"drdxt-test-etl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            var reste = Path.Combine(dossier, "drdxt-presents-4242-abc.etl");
            var voisin = Path.Combine(dossier, "autre-outil.etl");
            File.WriteAllText(reste, "x");
            File.WriteAllText(voisin, "x");

            CompteurPresentsDxgi.RangerRestes(dossier).Should().Be(1);

            File.Exists(reste).Should().BeFalse();
            File.Exists(voisin).Should().BeTrue();
        }
        finally { Directory.Delete(dossier, recursive: true); }
    }

    [Fact]
    public async Task Un_succes_de_PresentMon_passe_tel_quel_et_le_compteur_n_est_jamais_appele()
    {
        var dxgiAppele = false;
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Ok(Serie(8))),
            (_, _, _) => { dxgiAppele = true; return Result<BenchmarkResult>.Ok(Serie(5)); })
            .RunAsync(1, TimeSpan.FromSeconds(1), 1, CancellationToken.None);
        r.Value!.Instrument.Should().BeNull();
        r.Value.AvgFps.Should().BeApproximately(125, 0.01);
        dxgiAppele.Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCodes.CaptureFail)]
    [InlineData(ErrorCodes.NoPresentingFrames)]
    [InlineData(ErrorCodes.PmonUnavailable)]
    public async Task Un_echec_de_capture_bascule_sur_DXGI_et_le_resultat_le_dit(string code)
    {
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(code, "x")),
            (_, _, _) => Result<BenchmarkResult>.Ok(Serie(5)))
            .RunAsync(1, TimeSpan.FromSeconds(1), 3, CancellationToken.None);
        r.IsOk.Should().BeTrue();
        r.Value!.Instrument.Should().Be("DXGI");
        r.Value.AvgFps.Should().BeApproximately(200, 0.01);
        r.Value.Runs.Should().Be(3);
        r.Value.ForegroundFraction.Should().BeNull("sans sonde de premier plan : non observe, jamais 0 %");
    }

    // 26/09/2026 : une seule passe DXGI rendait une marge de 0 ; l'A/B voyait alors dans le moindre
    // ecart un gain « mesurable ». Le chemin PresentMon rend +inf sous trois passes : meme regle ici.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Moins_de_trois_passes_DXGI_ne_resolvent_rien(int passes)
    {
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, "x")),
            (_, _, _) => Result<BenchmarkResult>.Ok(Serie(5)))
            .RunAsync(1, TimeSpan.FromSeconds(1), passes, CancellationToken.None);

        r.Value!.MarginOfError.Should().Be(double.PositiveInfinity);
    }

    // Chaque low a sa marge, tirée de SA dispersion entre passes : le 1 % low varie ici, le 0,1 % low non.
    // Deux listes croisées donneraient l'inverse.
    [Fact]
    public async Task Chaque_low_du_repli_DXGI_a_sa_propre_marge_entre_passes()
    {
        var lows = new Queue<double>(new[] { 100.0, 120.0, 140.0 });
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, "x")),
            (_, _, _) => Result<BenchmarkResult>.Ok(new BenchmarkResult(200, lows.Dequeue(), 50, new List<double> { 5.0 }, null, 0, 1)))
            .RunAsync(1, TimeSpan.FromSeconds(1), 3, CancellationToken.None);

        r.Value!.P1LowMarginOfError.Should().BeGreaterThan(0);
        double.IsFinite(r.Value.P1LowMarginOfError).Should().BeTrue();
        r.Value.P01LowMarginOfError.Should().Be(0, "le 0,1 % low n'a pas bougé d'une passe à l'autre");
    }

    private sealed class PremierPlan(int pid) : IForegroundProbe
    {
        public int? ForegroundProcessId() => pid;
    }

    [Theory]
    [InlineData(42, 1.0)]   // le jeu est resté devant pendant toute la passe
    [InlineData(7, 0.0)]    // une autre fenêtre était devant (alt-tab) : la passe le dit
    public async Task Le_repli_DXGI_observe_le_premier_plan_pendant_la_passe(int devant, double fraction)
    {
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, "x")),
            (_, _, _) => { Thread.Sleep(600); return Result<BenchmarkResult>.Ok(Serie(5)); },
            new PremierPlan(devant))
            .RunAsync(42, TimeSpan.FromSeconds(1), 1, CancellationToken.None);

        r.Value!.Instrument.Should().Be("DXGI");
        r.Value.ForegroundFraction.Should().Be(fraction);
    }

    [Fact]
    public async Task Un_echec_qui_n_est_pas_de_capture_ne_bascule_pas()
    {
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "pid")),
            (_, _, _) => throw new InvalidOperationException("ne doit pas etre appele"))
            .RunAsync(1, TimeSpan.FromSeconds(1), 1, CancellationToken.None);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PidInvalid);
    }

    [Fact]
    public async Task Si_le_compteur_echoue_aussi_l_echec_dit_les_deux()
    {
        var r = await new RepliDxgiBenchmarkRunner(new PresentMon(Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, "pm")),
            (_, _, _) => Result<BenchmarkResult>.Fail(ErrorCodes.NoPresentingFrames, "0 image"))
            .RunAsync(1, TimeSpan.FromSeconds(1), 1, CancellationToken.None);
        r.Error!.Value.Code.Should().Be(ErrorCodes.NoPresentingFrames);
        r.Error.Value.Message.Should().Contain(ErrorCodes.CaptureFail);
    }
}
