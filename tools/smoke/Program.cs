using DrDxt.Core;
using DrDxt.Measurement;
using Microsoft.Extensions.DependencyInjection;

// Smoke harness: capture a real process's frametimes through DrDXT's OWN adapter
// (PresentMonSession + BenchmarkRunner), NON-elevated — proving the product model
// (PresentMon service elevated once, measurement client runs as a normal user).
// Requires the PresentMon service running. See ../../third_party/presentmon/SMOKE-TEST.md.
if (args.Length < 1)
{
    Console.WriteLine("usage: smoke <pid> [seconds] [runs] [sliceSeconds]");
    return;
}

// A/B REPLAY (V4.0, 05/09/2026) — DEUX series enregistrees -> le verdict d'AbComparer.
//
// Le rejeu d'UNE serie existait deja. Ce qui n'existait pas, et que rien dans ce depot ne
// faisait : presenter DEUX series a AbComparer. L'instrument qui decide « gain ou pas gain »
// n'avait jamais ete pointe sur des donnees enregistrees — on ne pouvait donc le verifier
// qu'en jouant une partie, c'est-a-dire jamais.
//
// La marge vient des TRANCHES de la serie (>= 3 pour etre finie) : sans elle, AbComparer
// compare deux moyennes sans savoir ce que le bruit autorise a conclure.
if (args.Length >= 2 && !int.TryParse(args[0], out _) && File.Exists(args[0]) && File.Exists(args[1]))
{
    // La taille de tranche est un ARGUMENT, et ce n'est pas du confort. Mesure du 05/09 : avec
    // les 120 s par defaut, chacune des 8 series enregistrees ne rend qu'UNE tranche, la marge
    // inter-tranches vaut +∞, et le comparateur ne peut alors rendre que « NoMeasurableGain ».
    // Un instrument qui n'a qu'une reponse possible ne prouve rien — il faut au moins 3 tranches
    // pour que la marge soit finie, donc un decoupage adapte a la duree reelle des captures.
    var slice = args.Length >= 3 && int.TryParse(args[2], out var s) ? s : 120;
    var before = FromFile(args[0], slice);
    var after = FromFile(args[1], slice);
    var verdict = AbComparer.Compare(before, after);

    Console.WriteLine($"BEFORE {Path.GetFileName(args[0])}: avg={before.AvgFps:F1} p1Low={before.P1LowFps:F1} " +
                      $"p01Low={before.P01LowFps:F1} moE=±{before.MarginOfError:F2} tranches={before.Runs}");
    Console.WriteLine($"AFTER  {Path.GetFileName(args[1])}: avg={after.AvgFps:F1} p1Low={after.P1LowFps:F1} " +
                      $"p01Low={after.P01LowFps:F1} moE=±{after.MarginOfError:F2} tranches={after.Runs}");
    Console.WriteLine($"DELTA  avg={after.AvgFps - before.AvgFps:+0.0;-0.0} " +
                      $"p1Low={after.P1LowFps - before.P1LowFps:+0.0;-0.0} " +
                      $"p01Low={after.P01LowFps - before.P01LowFps:+0.0;-0.0}");
    Console.WriteLine($"VERDICT: {(verdict.IsMeasurableGain ? "MeasurableGain" : "NoMeasurableGain")} — {verdict.SummaryKey}");
    return;
}

static BenchmarkResult FromFile(string path, int sliceSeconds = 120)
{
    var frames = File.ReadAllLines(path)
        .Select(l => double.TryParse(l, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
        .Where(double.IsFinite).ToList();

    var clean = FrameSeriesHygiene.Clean(frames);
    var used = clean.Applied ? clean.FrameTimesMs : frames;
    var perSlice = FrameSeriesHygiene.Slice(used, sliceSeconds).Select(FrametimeStatistics.AvgFps).ToList();

    // Meme point de passage que le banc de rejeu : construit a la main et en positionnel, il
    // perdait le p95 et le p99 en silence (le contrat les laisse a null par defaut).
    return FrametimeStatistics.PasseDepuisSerie(
        used, Math.Max(perSlice.Count, 1), FrametimeStatistics.InterRunMarginOfError(perSlice));
}

// Re-analysis mode: a path instead of a pid replays a SAVED frame series through the current
// hygiene code. The raw frames are dumped on every capture precisely so an analysis change can
// be re-run on real data without asking anyone to play another match.
if (!int.TryParse(args[0], out _) && File.Exists(args[0]))
{
    var saved = File.ReadAllLines(args[0])
        .Select(l => double.TryParse(l, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
        .Where(double.IsFinite).ToList();
    Console.WriteLine($"Re-analysing {saved.Count} saved frames from {args[0]}");
    Report(saved, args.Length > 1 ? int.Parse(args[1]) : 120);
    return;
}

int pid = int.Parse(args[0]);
int secs = args.Length > 1 ? int.Parse(args[1]) : 8;
int runs = args.Length > 2 ? int.Parse(args[2]) : 1;
int sliceSecs = args.Length > 3 ? int.Parse(args[3]) : 120;

// Le banc du produit sait desormais reparer son propre service de mesure, donc il depend du
// canal eleve. Ce harnais tourne NON eleve, exprès : il declare un canal qui refuse, pour que
// le smoke mesure la machine telle qu'elle est et ne la repare pas au passage.
var sp = new ServiceCollection()
    .AddSingleton<IElevatedClient>(new RefusDElevation())
    .AddDrDxtMeasurement()
    .BuildServiceProvider();
var runner = sp.GetRequiredService<IBenchmarkRunner>();

Console.WriteLine($"Capturing PID {pid}: {runs} run(s) x {secs}s via DrDXT BenchmarkRunner (non-elevated)...");
var r = await runner.RunAsync(pid, TimeSpan.FromSeconds(secs), runs, default);

if (r.IsOk)
{
    var b = r.Value!;
    var lat = b.EstimatedPcLatencyMs is { } l ? $"{l:F1}ms" : "n/a";
    var busy = b.GpuBusyPercent is { } g ? $"{g:F0}%" : "n/a";
    Console.WriteLine($"RESULT OK: frames={b.FrameTimesMs.Count} avgFps={b.AvgFps:F1} " +
                      $"p1Low={b.P1LowFps:F1} p01Low={b.P01LowFps:F1} renderLatency={lat} gpuBusy={busy} " +
                      $"moE=±{b.MarginOfError:F2} runs={b.Runs}");

    // LES CINQ GRANDEURS DE CADENCE N'ETAIENT LISIBLES QUE PAR `tools/optimize` — QUI ECRIT.
    //
    // `BenchmarkResult` les porte depuis des semaines. `renderLatency` et `gpuBusy` ci-dessus sont
    // deux ANCIENS agregats, pas celles-la. Resultat : le seul chemin qui les affichait etait celui
    // qui applique un reglage, donc inutilisable pour simplement REGARDER une session — et c'est
    // exactement ce qu'il faut faire avant de toucher a quoi que ce soit.
    //
    // `GpuWaitRatio` dit la part de frame ou le GPU ATTEND et `CpuBusyRatio` le travail CPU de
    // l'application : ensemble, ils repondent a « pourquoi le GPU reste-t-il a 88-90 % ? » sans
    // qu'aucune session ETW soit necessaire.
    static string Pct(double? v) => v is { } x ? $"{x:P0}" : "non lu";
    static string Ms(double? v) => v is { } x ? $"{x:0.00}ms" : "non lu";

    Console.WriteLine($"CADENCE: cpuApp={Pct(b.CpuBusyRatio)} gpuWait={Pct(b.GpuWaitRatio)} "
                      + $"presentStall_p95={Ms(b.PresentStallMsP95)} "
                      + $"renderToPresent_p95={Ms(b.RenderPresentLatencyMsP95)} "
                      + $"animationError_p95={Ms(b.AnimationErrorMsP95)}");

    // LE PLAFOND DU GAIN, calcule par le PRODUIT sur cette capture. L'outil n'a pas sa propre
    // arithmetique : il imprime la phrase que l'ecran affiche apres un scan, ou le silence.
    var borne = PlafondGpu.DepuisLaMesure(b.GpuBusyPercent);
    Console.WriteLine(borne is null
        ? "PLAFOND: rien a dire — cette capture n'a pas rendu de GPU-busy"
        : $"PLAFOND: {borne.Lisible}");
    // L'erreur d'animation est la seule qui sache dire qu'une moyenne de 140 FPS est
    // desagreable a jouer. « non lu » n'est PAS zero : c'est une grandeur que cette capture n'a
    // pas pu produire.

    // ET CES TROIS p95 NE VALENT RIEN SUR UNE CAPTURE COURTE — trouve en regardant la premiere
    // sortie reelle. Huit frames de terminal ont rendu `animationError_p95 = 1186,95 ms` : un
    // chiffre juste, plausible, et vide de sens, puisqu'a 0,8 image par seconde il ne fait que
    // recopier l'intervalle entre deux images. Le dire coute une ligne ; le taire laisse quelqu'un
    // conclure a une saccade d'une seconde.
    // Le compte des TRAMES ne vaut que pour les deux premiers p95 : celui de l'erreur
    // d'animation repose sur une serie a part, et le produit le juge sur son propre compte
    // (`AnimationErrorSamples`). Ici on le dit plutot que de laisser croire que les trois sont
    // couverts par la meme garde.
    if (TailStatistic.PourquoiPasInterpretable(b.AnimationErrorSamples ?? 0) is { } raisonCadence)
        Console.WriteLine($"   l'erreur d'animation n'est PAS interpretable — {raisonCadence}");
    // 27/09/2026 : les trames EXPLOITABLES, la liste meme sur laquelle les p95 sont calcules.
    if (TailStatistic.PourquoiPasInterpretable(FrametimeStatistics.EchantillonsExploitables(b.FrameTimesMs)) is { } raison)
        Console.WriteLine($"   les trois p95 ci-dessus ne sont PAS interpretables — {raison}");

    Report(b.FrameTimesMs, sliceSecs);

}
else
{
    Console.WriteLine($"RESULT FAIL: {r.Error!.Value.Code} — {r.Error.Value.Message}");
}

void Report(IReadOnlyList<double> frames, int sliceSeconds)
{
    // --- Raw frames to disk, then the hygiene pass on the REAL series -------------------
    // The aggregates above are computed on the RAW capture: loading and alt-tab included.
    var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "p3");
    Directory.CreateDirectory(dir);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var rawPath = Path.GetFullPath(Path.Combine(dir, $"frames-{stamp}.txt"));
    File.WriteAllLines(rawPath, frames.Select(x => x.ToString("R",
        System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine($"frames written: {rawPath}");

    var clean = FrameSeriesHygiene.Clean(frames);
    Console.WriteLine(clean.Applied
        ? $"HYGIENE: kept {clean.FrameTimesMs.Count}/{frames.Count} frames " +
          $"(warmup {clean.WarmupSecondsDropped}s, out-of-focus {clean.OutOfFocusSecondsDropped}s)"
        : $"HYGIENE: NOT APPLIED (would have kept too little — series returned untouched)");

    var used = clean.Applied ? clean.FrameTimesMs : frames;
    // LA SACCADE, telle que l'ecran la dit : le p95 des frametimes, sur la serie NETTOYEE.
    var lentes = FrametimeStatistics.PercentileOrNull(used, 0.95);
    Console.WriteLine(lentes is { } ms
        ? $"SACCADE: les 5 % de trames les plus lentes durent AU MOINS {ms:0.#} ms "
          + $"(sur {FrametimeStatistics.EchantillonsExploitables(used)} trames exploitables)"
        : "SACCADE: rien a dire — aucune trame exploitable");

    Console.WriteLine($"CLEAN: avgFps={FrametimeStatistics.AvgFps(used):F1} " +
                      $"p1Low={FrametimeStatistics.P1LowFps(used):F1} " +
                      $"p01Low={FrametimeStatistics.P01LowFps(used):F1}");

    var slices = FrameSeriesHygiene.Slice(used, sliceSeconds);
    if (slices.Count == 0)
    {
        Console.WriteLine($"SLICES: none — clean series shorter than one {sliceSeconds}s slice");
    }
    else
    {
        var perSlice = slices.Select(FrametimeStatistics.AvgFps).ToList();
        var moe = FrametimeStatistics.InterRunMarginOfError(perSlice);
        Console.WriteLine($"SLICES: {slices.Count} x {sliceSeconds}s  avgFps per slice = " +
                          string.Join(", ", perSlice.Select(x => x.ToString("F1"))));
        Console.WriteLine(double.IsInfinity(moe)
            ? "SLICE MARGIN: not provable (fewer than 3 slices)"
            : $"SLICE MARGIN: ±{moe:F2} fps = {100.0 * moe / perSlice.Average():F1} % " +
              "— the smallest gain this session could prove");
    }
}

/// <summary>Un canal eleve qui dit NON, et qui le dit. Ce harnais observe, il ne repare pas :
/// une mesure qui modifie la machine qu'elle mesure ne mesure plus rien.</summary>
internal sealed class RefusDElevation : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed,
            $"tools/smoke tourne sans elevation, volontairement : {op.GetType().Name} refusee."));
}
