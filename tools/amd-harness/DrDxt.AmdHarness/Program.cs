using DrDxt.AmdHarness;
using DrDxt.AmdHarness.Reporting;
using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.Measurement;
using Microsoft.Extensions.DependencyInjection;

// -----------------------------------------------------------------------
// DrDxt.AmdHarness — portable AMD test-harness console entry point.
//
// Task 5a: real read-side bricks wired via a minimal DI container
// (Detection + Measurement only — no App layer, no privileged write path):
//   - IHardwareDetector    → HardwareDetector (WMI + DXGI, DrDxt.Detection)
//   - ISettingsStateReader → SettingsStateReader (DrDxt.Detection)
//   - IScanScorer          → ScanScorer (DrDxt.Detection)
//   - IBenchmarkRunner     → BenchmarkRunner (PresentMon-backed, DrDxt.Measurement);
//       fails clean when PresentMon is absent or no game is running — expected on
//       a test box; bench steps recorded as non-fatal outcomes.
//   - IAdlxGpuTuning + IAdlxGpuTuningWriter → real AdlxGpuTuningReader /
//       AdlxGpuTuningWriter (Tasks 3/4); ADLX gate skips cleanly on non-AMD machine.
//   - IHarnessCleanupVerifier → RealHarnessCleanupVerifier (Task 5a); inspects
//       SCM + Task Scheduler + residual paths honestly via small Win32 seams.
//
// HONEST STUBS (out of scope for this harness — validated separately):
//   ITweakEngine / ISafetyManager — the safe non-GPU tweak apply path (power-plan,
//   latency, X3D) is validated by the full product smoke suite. This harness focuses
//   on the AMD-GPU path: detect → scan → bench → ADLX gate → write → revert.
//   These stubs refuse cleanly and surface an honest message; they never claim success.
//
// Run: dotnet run -- [safe-tweak-ids] [gpu-tweak-ids]
// -----------------------------------------------------------------------

// Revue n° 8 : « --etape N » = une seule étape, dans ce processus enfant ; sa sortie est relue par le parent.
if (args.Length == 2 && args[0] == "--etape" && int.TryParse(args[1], out var numero))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var servicesEtape = new ServiceCollection();
    servicesEtape.AddSingleton<IElevatedClient>(new RefusDElevation());
    servicesEtape.AddDrDxtDetection();
    using var fournisseurEtape = servicesEtape.BuildServiceProvider();
    var toutes = RapportLecture.EtapesReelles(fournisseurEtape.GetRequiredService<IHardwareDetector>(), fournisseurEtape.GetRequiredService<ISettingsStateReader>());
    if (numero < 0 || numero >= toutes.Count) { Console.WriteLine($"étape {numero} inconnue"); return; }
    // Pour éprouver le rapport : DRDXT_BANC_PLANTER=N tue l'étape N net, comme un plantage du pilote.
    if (Environment.GetEnvironmentVariable("DRDXT_BANC_PLANTER") == numero.ToString(System.Globalization.CultureInfo.InvariantCulture))
        Environment.FailFast("plantage simulé de l'étape (DRDXT_BANC_PLANTER)");
    Console.WriteLine(await toutes[numero].Lire(CancellationToken.None));
    return;
}

// 07/10/2026 (revue n° 8) : « --valider » pose chaque levier du produit, le relit, le rend, le relit encore.
// « --remettre » rend seulement ce qu'un essai interrompu a laissé. Les deux commencent par rendre ce qui reste.
if (args.Contains("--valider") || args.Contains("--remettre"))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var valider = args.Contains("--valider");
    var chemin = Path.Combine(RapportLecture.Dossier(), $"DrDXT-validation-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    void Ecrire(string t) { try { File.AppendAllText(chemin, t, new System.Text.UTF8Encoding(false)); } catch (Exception) { } Console.Write(t); }
    Ecrire($"DrDXT — {(valider ? "validation complète : chaque réglage est posé puis remis d'origine" : "remise d'origine")}{Environment.NewLine}"
        + $"Machine : {Environment.MachineName} | {DateTime.Now:dd/MM/yyyy HH:mm:ss} | banc {typeof(RapportLecture).Assembly.GetName().Version}{Environment.NewLine}{Environment.NewLine}");
    var nonRemis = new List<string>();
    try
    {
        using var sp = ValidationMachine.Services();
        var remise = await ValidationMachine.RemettreAsync(sp);
        Ecrire($"=== essai précédent{Environment.NewLine}{remise.Texte}{Environment.NewLine}{Environment.NewLine}");
        if (!remise.Propre) nonRemis.Add(remise.Texte);
        else if (valider)
        {
            var r = await ValidationMachine.ValiderAsync(sp, Ecrire);
            nonRemis.AddRange(r.NonRemis);
        }
    }
    catch (Exception ex) { Ecrire($"arrêt inattendu : {ex.Message}{Environment.NewLine}"); nonRemis.Add("arrêt inattendu : relance le banc, il remet d'abord ce qui reste"); }
    var bilan = nonRemis.Count == 0
        ? "BILAN : tout est remis d'origine."
        : $"BILAN : À VÉRIFIER À LA MAIN :{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", nonRemis);
    // Le bilan en tête du fichier : c'est la seule ligne que le joueur doit lire.
    try { File.WriteAllText(chemin, bilan + Environment.NewLine + Environment.NewLine + File.ReadAllText(chemin), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
    Console.WriteLine();
    Console.WriteLine(bilan);
    Console.WriteLine($"Rapport écrit : {chemin}");
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Appuie sur une touche pour fermer.");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { }
    }
    return;
}

// 07/10/2026 (T3) : par défaut, le rapport en LECTURE SEULE (Reporting/RapportLecture.cs), écrit dans Téléchargements,
// qui ne s'arrête jamais sur une erreur. L'ancienne session (écriture de fréquence et de tension sur la carte AMD) est
// RETIRÉE : la règle du 29/09 interdit tension, fréquence et ventilateur. Elle ne se lance plus, même demandée.
if (!args.Contains("--lire"))
{
    if (args.Length > 0 && !args.Contains("--rapport"))
        Console.WriteLine("L'ancienne session d'écriture sur la carte est retirée (règle du 29/09 : jamais tension, fréquence ni ventilateur). Rapport en lecture seule à la place.");
    Console.WriteLine("DrDXT — rapport de test en LECTURE SEULE : rien n'est modifié sur ce PC. Compte 1 à 3 minutes. Ne ferme pas cette fenêtre.");
    Console.WriteLine();
    var entete = $"DrDXT — rapport de test en lecture seule{Environment.NewLine}Machine : {Environment.MachineName} | {DateTime.Now:dd/MM/yyyy HH:mm:ss} | banc {typeof(RapportLecture).Assembly.GetName().Version}{Environment.NewLine}{Environment.NewLine}";
    var chemin = Path.Combine(RapportLecture.Dossier(), $"DrDXT-rapport-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    var ecrit = true;
    try { File.WriteAllText(chemin, entete, new System.Text.UTF8Encoding(true)); }
    catch (Exception) { ecrit = false; }
    string texte;
    try
    {
        // Revue n° 8 : le parent n'appelle aucun code natif ; chaque étape tourne dans son processus.
        var services = new ServiceCollection();
        services.AddSingleton<IElevatedClient>(new RefusDElevation());
        services.AddDrDxtDetection();
        using var fournisseur = services.BuildServiceProvider();
        var noms = RapportLecture.EtapesReelles(fournisseur.GetRequiredService<IHardwareDetector>(), fournisseur.GetRequiredService<ISettingsStateReader>())
            .Select(e => e.Nom).ToList();
        texte = await RapportLecture.DeroulerAsync(RapportLecture.EtapesEnProcessus(noms), RapportLecture.DelaiParEtape,
            nom => Console.WriteLine($"  … {nom}"),
            bloc => { if (ecrit) File.AppendAllText(chemin, bloc, new System.Text.UTF8Encoding(false)); });
        if (ecrit) File.AppendAllText(chemin, texte.Split(Environment.NewLine)[0] + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }
    catch (Exception ex)
    {
        texte = $"Le rapport n'a pas pu se dérouler : {ex}";
        try { if (ecrit) File.AppendAllText(chemin, texte); } catch (Exception) { }
    }
    if (ecrit)
    {
        Console.WriteLine();
        Console.WriteLine($"Rapport écrit : {chemin}");
        Console.WriteLine("Envoie ce fichier à la personne qui t'a donné ce test, et à personne d'autre : il contient le nom de ton PC et la liste de ses programmes. Rien n'a été modifié sur ton PC.");
    }
    else
    {
        Console.WriteLine("Le fichier n'a pas pu s'écrire. Copie tout ce texte et envoie-le :");
        Console.WriteLine(entete + texte);
    }
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.WriteLine("Appuie sur une touche pour fermer.");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { }
    }
    return;
}

// A1 (06/10/2026) : « --lire » lit par ADLX, sur la carte AMD dédiée, ce que le produit affiche (SAM, Anti-Lag,
// FRTC / Chill, température), puis s'arrête. Lecture seule : aucune entrée Set* n'est appelée, aucun service lancé.
if (args.Contains("--lire"))
{
    static string Etat(bool? v) => v switch { true => "activé", false => "désactivé", null => "non lu" };
    var lus = AdlxReglages.Lire();
    Console.WriteLine($"amdadlx64.dll dans System32 : {(AdlxReglages.DllPresente() ? "présente" : "absente")}");
    Console.WriteLine(lus.Raison is null ? "ADLX : carte dédiée trouvée" : $"ADLX : rien lu ({lus.Raison})");
    Console.WriteLine($"SAM (ReBAR)   : {Etat(lus.Sam)}");
    Console.WriteLine($"Anti-Lag      : {Etat(lus.AntiLag)}");
    Console.WriteLine($"FRTC          : {Etat(lus.FrtcActif)}{(lus.FrtcFps is { } f ? $", {f} images/s" : "")}");
    Console.WriteLine($"Chill         : {Etat(lus.ChillActif)}{(lus.ChillMaxFps is { } c ? $", {lus.ChillMinFps}-{c} images/s" : "")}");
    Console.WriteLine($"Limite images : {(lus.LimiteImages is { } l ? (l == 0 ? "aucune" : $"{l} images/s") : "non lue")}");
    Console.WriteLine($"Température   : {(lus.TemperatureC is { } t ? $"{t:0.0} °C" : "non lue")}");
    return;
}

Console.WriteLine("DrDxt AMD Harness — portable test (service-less, leaves nothing behind)");
Console.WriteLine("Seams (Task 5a): real detector / state-reader / scanner / bench / cleanup-verifier");
Console.WriteLine("              + real ADLX reader + writer (Tasks 3/4, fail-clean when no AMD GPU)");
Console.WriteLine();

var safeTweakIds = args.Length > 0 ? args[0].Split(',') : new[] { "power.plan", "latency.irq" };
var gpuTweakIds  = args.Length > 1 ? args[1].Split(',') : new[] { "gpu.undervolt" };

// ── Share one AdlxNative between reader and writer (single init, single terminate) ─
var adlxNative = new AdlxNative();

// Auto-select the AMD GPU that actually supports manual GFX tuning. On an APU + dGPU
// system (e.g. Ryzen 7800X3D iGPU + Radeon RX 9070 XT) ADLX index 0 is often the iGPU,
// which does NOT support manual tuning — the discrete card is index 1. Probe each GPU
// (read-only) and use the FIRST whose GFX tuning reads cleanly. Falls back to 0 so a
// non-AMD / no-tunable-GPU machine still skips the gate cleanly with a diagnostic.
int gpuIndex = 0;
{
    var picker = new AdlxGpuTuningReader(adlxNative);
    if (picker.IsAvailable)
    {
        for (int i = 0; i < 8; i++)
        {
            if (picker.GetUndervoltValue(i).IsOk) { gpuIndex = i; break; }
        }
    }
}
Console.WriteLine($"AMD GPU index selected for tuning: {gpuIndex} (auto: first GPU supporting manual GFX tuning).");
Console.WriteLine();

// Per-GPU ADLX diagnostic dump (read-only): shows EXACTLY where each GPU's GFX read
// path stops. Essential on iGPU + dGPU systems to see which card is tunable and why
// (e.g. iGPU reports unsupported, dGPU stops at QI-Tuning2, etc.).
Console.WriteLine("Per-GPU ADLX diagnostic (read-only):");
for (int i = 0; i < 4; i++)
{
    var probe = adlxNative.ProbeGfxPath(i);
    bool outOfRange = false;
    Console.WriteLine($"  GPU {i}:");
    foreach (var s in probe)
    {
        Console.WriteLine($"    [{(s.Ok ? "OK" : "!!")}] {s.Name}{(s.Detail is null ? "" : $" — {s.Detail}")}");
        if (s.Name == "gpu-count" && !s.Ok) outOfRange = true;
    }
    if (outOfRange) break; // no GPU at this index — stop probing higher indices
}
Console.WriteLine();

var config = new HarnessConfig(
    SafeTweakIds: safeTweakIds,
    GpuTweakIds:  gpuTweakIds,
    GpuIndex:     gpuIndex,
    TargetPid:    0);

// ── Minimal DI container for real read-side bricks ───────────────────────────
// Only Detection + Measurement: no Application layer, no privileged write path.
// The safe tweak apply path (ITweakEngine / ISafetyManager) stays as honest stubs
// below so that scope is explicit and nothing privileged runs accidentally.
var svcCollection = new ServiceCollection();
// Le banc du produit sait desormais reparer son propre service de mesure : il depend donc du
// canal eleve. Ce harnais n'ouvre AUCUN chemin privilegie -- il declare un canal qui refuse,
// ce qui rend le refus visible dans la sortie au lieu de le rendre impossible a distinguer.
svcCollection.AddSingleton<IElevatedClient>(new RefusDElevation());
svcCollection.AddDrDxtDetection();    // HardwareDetector, SettingsStateReader, ScanScorer
svcCollection.AddDrDxtMeasurement(); // BenchmarkRunner (PresentMon-backed)
using var provider = svcCollection.BuildServiceProvider();

var runner = new HarnessRunner(
    detector:    provider.GetRequiredService<IHardwareDetector>(),    // Task 5a: real
    stateReader: provider.GetRequiredService<ISettingsStateReader>(), // Task 5a: real
    scanner:     provider.GetRequiredService<IScanScorer>(),          // Task 5a: real
    engine:      new StubTweakEngine(),    // honest stub — safe tweaks validated separately
    safety:      new StubSafetyManager(), // honest stub — safe tweaks validated separately
    adlx:        new AdlxGpuTuningReader(adlxNative),  // Task 3: real ADLX read seam
    writer:      new AdlxGpuTuningWriter(adlxNative),  // Task 4: real ADLX write seam
    bench:       provider.GetRequiredService<IBenchmarkRunner>(),     // Task 5a: real
    cleanup:     new RealHarnessCleanupVerifier(                      // Task 5a: real
                     new Win32ServiceInspector(),
                     new Win32TaskSchedulerInspector(),
                     new Win32ResidualPathInspector()),
    // Read-only ADLX diagnostic hook: explains a skipped GPU gate (where ADLX stopped).
    gfxDiagnostics: adlxNative.ProbeGfxPath);

var report = await runner.RunAsync(config, CancellationToken.None);

Console.WriteLine($"IsCompleted       : {report.IsCompleted}");
Console.WriteLine($"GpuGate           : {report.GpuGate}");
Console.WriteLine($"RevertCompleted   : {report.RevertCompleted}");
Console.WriteLine($"Hardware          : {(report.Hardware is null ? "(detection failed — see step log)" : report.Hardware.Cpu.Model)}");
Console.WriteLine($"CleanupReport     : {(report.CleanupReport is null ? "(none)" : $"service={report.CleanupReport.NoServiceInstalled} task={report.CleanupReport.NoScheduledTaskInstalled}")}");
Console.WriteLine();
Console.WriteLine("Steps:");
foreach (var step in report.StepOutcomes)
    Console.WriteLine($"  [{(step.Succeeded ? "OK" : "!!")}] {step.StepId}{(step.Note is null ? "" : $" — {step.Note}")}");

// ── Task 6: write structured report files ────────────────────────────────────
// Both files land next to the exe (or the script directory when run via dotnet run).
try
{
    string reportDir = AppContext.BaseDirectory;
    var (txtPath, htmlPath) = await HarnessReportRenderer.WriteAsync(report, reportDir);
    Console.WriteLine();
    Console.WriteLine($"Report (txt) : {txtPath}");
    Console.WriteLine($"Report (html): {htmlPath}");
}
catch (Exception ex)
{
    Console.WriteLine($"[report write failed: {ex.GetType().Name}: {ex.Message}]");
}

// -----------------------------------------------------------------------
// Honest stubs for the safe non-GPU tweak apply path.
//
// ITweakEngine and ISafetyManager are NOT wired in this harness. The safe
// tweaks (power-plan, latency, X3D) are validated separately via the full
// product smoke suite. This harness focuses solely on the AMD-GPU path:
//   detect → read-state → scan → bench → ADLX gate → write → revert.
//
// These stubs refuse cleanly (Fail) with an honest message. The runner
// records each stub refusal as a step outcome — never a false success.
// -----------------------------------------------------------------------
file sealed class StubTweakEngine : ITweakEngine
{
    public Task<Result<ApplyReport>> ApplyAsync(TweakPlan plan, BenchmarkResult? baseline,
        int targetPid, CancellationToken ct) =>
        Task.FromResult(Result<ApplyReport>.Fail(
            ErrorCodes.HelperOpFailed,
            "Safe tweaks not exercised by this harness — validated separately as part of the product."));
}

file sealed class StubSafetyManager : ISafetyManager
{
    public Task<Result> EnsureRestorePointAsync() =>
        Task.FromResult(Result.Fail(
            ErrorCodes.RestoreDisabled,
            "Safe tweaks not exercised by this harness — validated separately as part of the product."));

    public Task<Result> RevertAsync(TweakSnapshot snapshot) =>
        Task.FromResult(Result.Fail(
            ErrorCodes.HelperOpFailed,
            "Safe tweaks not exercised by this harness — validated separately as part of the product."));
}

/// <summary>Un canal eleve qui refuse, et qui le dit. Le harnais AMD lit la machine, il ne la
/// modifie pas : c'est cette limite qui rend ses observations comparables entre deux passages.</summary>
internal sealed class RefusDElevation : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed,
            $"le harnais AMD n'ouvre aucun chemin privilegie : {op.GetType().Name} refusee."));
}
