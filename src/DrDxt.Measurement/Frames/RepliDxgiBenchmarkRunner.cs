using DrDxt.Core;

namespace DrDxt.Measurement.Frames;

/// <summary>
/// Le banc PresentMon d'abord ; s'il ne capture rien, le compteur DXGI de DrDXT.
///
/// Un succes de PresentMon passe tel quel. Seuls les echecs de capture basculent (service absent,
/// suivi refuse, zero image) : un pid invalide ou une annulation restent ce qu'ils sont. Le
/// resultat de repli porte <c>Instrument = "DXGI"</c>. Le premier plan y est observe comme sur le
/// chemin PresentMon (une lecture toutes les 250 ms pendant la passe) ; sans sonde, il reste inconnu.
/// </summary>
public sealed class RepliDxgiBenchmarkRunner : IBenchmarkRunner
{
    private static readonly string[] EchecsDeCapture =
        { ErrorCodes.CaptureFail, ErrorCodes.NoPresentingFrames, ErrorCodes.PmonUnavailable, ErrorCodes.NoFrames };

    private readonly IBenchmarkRunner _presentMon;
    private readonly Func<int, TimeSpan, CancellationToken, Result<BenchmarkResult>> _dxgi;
    private readonly IForegroundProbe? _foreground;

    public RepliDxgiBenchmarkRunner(IBenchmarkRunner presentMon,
        Func<int, TimeSpan, CancellationToken, Result<BenchmarkResult>> dxgi, IForegroundProbe? foreground = null)
        => (_presentMon, _dxgi, _foreground) = (presentMon, dxgi, foreground);

    /// <summary>Le banc enveloppe : les tests de composition verifient que l'auto-reparation y est.</summary>
    public IBenchmarkRunner PresentMon => _presentMon;

    public async Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct)
    {
        var pm = await _presentMon.RunAsync(targetPid, duration, runs, ct).ConfigureAwait(false);
        if (pm.IsOk || pm.Error is not { } e || !EchecsDeCapture.Contains(e.Code)) return pm;

        var series = new List<double>();
        var focus = FocusObservation.Aucune;
        var parPasse = new List<double>();
        var lowsParPasse = new List<double>();
        var lows01ParPasse = new List<double>();
        for (var i = 0; i < Math.Max(1, runs); i++)
        {
            // Echantillonne pendant la passe : lu apres, le focus decrit le retour au jeu, pas l'alt-tab.
            using var arret = new CancellationTokenSource();
            var suivi = _foreground is null ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!arret.Token.IsCancellationRequested)
                {
                    focus = focus.Avec(_foreground.ForegroundProcessId(), targetPid);
                    try { await Task.Delay(250, arret.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }, CancellationToken.None);
            var r = await Task.Run(() => _dxgi(targetPid, duration, ct), ct).ConfigureAwait(false);
            arret.Cancel();
            await suivi.ConfigureAwait(false);
            if (!r.IsOk || r.Value is null) return r.IsOk ? pm : Result<BenchmarkResult>.Fail(r.Error!.Value.Code,
                $"{r.Error!.Value.Message} (PresentMon avant lui : {e.Code})");
            series.AddRange(r.Value.FrameTimesMs);
            parPasse.Add(r.Value.AvgFps);
            lowsParPasse.Add(r.Value.P1LowFps);
            lows01ParPasse.Add(r.Value.P01LowFps);
        }

        // Une seule passe rendait une marge de 0, et l'A/B prenait le moindre ecart pour un
        // gain. Meme regle que le chemin PresentMon : +inf sous trois passes.
        var marge = FrametimeStatistics.InterRunMarginOfError(parPasse);
        return Result<BenchmarkResult>.Ok(
            FrametimeStatistics.PasseDepuisSerie(series, parPasse.Count, marge)
                with { Instrument = "DXGI", ForegroundFraction = focus.FractionAuPremierPlan,
                       P1LowMarginOfError = FrametimeStatistics.InterRunMarginOfError(lowsParPasse),
                       P01LowMarginOfError = FrametimeStatistics.InterRunMarginOfError(lows01ParPasse) });
    }
}
