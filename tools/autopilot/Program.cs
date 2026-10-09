using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.ElevatedHelper.Execution.NvApi;

// ============================================================================
// LE PREMIER AUTOPILOTE REEL DE DrDXT.
//
// La boucle complete, sur la vraie machine, avec de vraies ecritures :
//
//   A/A          -> ce que cette scene sait resoudre (SceneResolution)
//   candidat     -> applique par NVAPI
//   read-back    -> ce que la carte a REELLEMENT pris
//   A-B-A-B      -> l'effet du reglage, degage de la derive (PairedAb)
//   verdict      -> avec sa resolution (Evidence)
//   garde/annule -> et la machine FINIT dans le meilleur etat prouve
//
// L'instrument de mesure est le banc deterministe (`GpuBench` + checksum a reponse
// connue) : sa resolution mesuree est **0,122 %** sur dix passes, contre ~13 % pour un
// rejeu in-game. C'est le seul instrument du projet capable de trancher un micro-gain.
//
// ELEVATION REQUISE : l'ecriture de l'offset passe par NVAPI. La mesure, non.
//
//   dotnet run --project tools/autopilot -- [offsetMhz] [secondesParPasse]
// ============================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var offsetCible = args.Length > 0 && int.TryParse(args[0], out var o) ? o : 100;
var secondes = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 5.0;
const int GpuIndex = 0;

Console.WriteLine("== AUTOPILOTE DrDXT — boucle complete sur la machine reelle ==");
Console.WriteLine($"   candidat  : offset coeur GPU {offsetCible:+#;-#;0} MHz");
Console.WriteLine($"   instrument: banc deterministe, {secondes:0.#} s par passe (resolution mesuree 0,122 %)");
Console.WriteLine();

var tuning = new NvApiTuningActions();
var bench = new GpuBench(new VorticeGpuComputeRunner(), GpuBenchWorkload.Default);

// L'etat de depart, releve AVANT toute ecriture : c'est vers lui qu'on revient si tout casse.
var offsetInitial = tuning.GetCoreClockOffsetMhz(GpuIndex);
Console.WriteLine(offsetInitial.IsOk
    ? $"   etat initial : offset = {offsetInitial.Value:+#;-#;0} MHz"
    : $"   offset illisible ({offsetInitial.Error?.Code}) — sans relecture, aucune mesure n'a de sens.");
if (!offsetInitial.IsOk) return 2;

var candidat = new GpuCoreOffsetCandidate(tuning, GpuIndex, offsetCible, offsetInitial.Value);
var passes = 0;

double Mesurer()
{
    var r = bench.Run(GpuIndex, TimeSpan.FromSeconds(secondes), CancellationToken.None);
    passes++;
    if (!r.IsOk || r.Value is null)
    {
        Console.WriteLine($"      passe {passes,2} : ECHEC ({r.Error?.Code})");
        return 0;  // une passe qui echoue n'est pas une mesure basse : elle ne compte pas.
    }

    // Le checksum a REPONSE CONNUE est la garde qui distingue « la carte a calcule vite » de
    // « la carte a calcule FAUX » — un overclock instable produit exactement les deux, et seul le
    // checksum les separe. Une passe au resultat faux ne compte pas comme une mesure basse : elle
    // ne compte pas du tout.
    var ok = r.Value.Stability == StabilityOutcome.Stable && r.Value.ChecksumOk;
    Console.WriteLine($"      passe {passes,2} : debit = {r.Value.ElementIterationsPerSecond:0.000e+00}"
                      + $"  checksum = {(r.Value.ChecksumOk ? "OK" : "FAUX")}  stabilite = {r.Value.Stability}");
    return ok ? r.Value.ElementIterationsPerSecond : 0;
}

var profil = AutopilotEngine.Run(
    new IAutopilotCandidate[] { candidat },
    Mesurer,
    passesAa: 3,
    stable: () => true);   // le banc verifie deja le checksum a chaque passe.

Console.WriteLine();
Console.WriteLine("== CE QUE LA SCENE POUVAIT VOIR ==");
Console.WriteLine($"   resolution : ±{profil.Scene.ResolutionPct:0.000} %   derive : {profil.Scene.DriftPctPerRun:+0.000;-0.000} %/passe"
                  + $"   verdict : {profil.Scene.Verdict}");

Console.WriteLine();
Console.WriteLine("== PROFIL ==");
foreach (var r in profil.Results)
{
    var e = r.Evidence;
    var chiffres = e is { } v && double.IsFinite(v.DeltaPct)
        ? $"{v.DeltaPct:+0.00;-0.00} % pour une resolution de ±{v.ResolutionPct:0.00} %"
        : "aucune mesure";
    Console.WriteLine($"   {r.Id,-20} {r.Outcome,-22} {chiffres}");
    if (!string.IsNullOrWhiteSpace(r.Detail)) Console.WriteLine($"   {"",-20} raison : {r.Detail}");
}

// ── LA GARANTIE : verifier par une RELECTURE INDEPENDANTE ou la carte a fini ──────────────
Console.WriteLine();
Console.WriteLine("== ETAT FINAL DE LA CARTE, RELU ==");
var final = new NvApiTuningActions().GetCoreClockOffsetMhz(GpuIndex);
var attendu = profil.Kept.Any() ? offsetCible : offsetInitial.Value;
Console.WriteLine($"   attendu : {attendu:+#;-#;0} MHz   relu : {(final.IsOk ? final.Value.ToString("+#;-#;0") : "?")} MHz");
Console.WriteLine(final.IsOk && final.Value == attendu
    ? "   la machine a fini dans l'etat que le profil annonce."
    : "   ECART : la machine n'est PAS dans l'etat annonce. C'est le pire cas possible.");

return final.IsOk && final.Value == attendu ? 0 : 1;

/// <summary>
/// L'offset coeur GPU, en candidat d'autopilote.
///
/// <see cref="ReadBack"/> compare la valeur RELUE a la valeur demandee, et pas seulement le
/// succes de l'ecriture. Mesure du 01/09/2026 : une limite de puissance « appliquee a 110 % » se
/// relisait a **100 %** — la carte n'a pas la marge, le pilote clampe en silence, et l'ecriture
/// rend Ok. Sans cette comparaison, l'autopilote mesurerait un etat qui n'existe pas.
/// </summary>
internal sealed class GpuCoreOffsetCandidate : IAutopilotCandidate
{
    private readonly NvApiTuningActions _tuning;
    private readonly int _gpu;
    private readonly int _cible;
    private readonly int _reference;

    public GpuCoreOffsetCandidate(NvApiTuningActions tuning, int gpu, int cible, int reference)
    {
        _tuning = tuning;
        _gpu = gpu;
        _cible = cible;
        _reference = reference;

        Descriptor = new CandidateDescriptor(
            "gpu.core_offset",
            CandidateKind.AutoHot,        // reversible a chaud, donc testable en A-B-A-B
            RiskLevel: 2,                 // un offset instable se voit au checksum, pas avant
            NeedsStability: true,
            AffectedMetrics: new[] { "gpu.debit", "fps.moyen" });
    }

    public CandidateDescriptor Descriptor { get; }

    public Result Apply() => _tuning.SetCoreClockOffset(_gpu, _cible);

    public Result Revert() => _tuning.SetCoreClockOffset(_gpu, _reference);

    public Result<bool> ReadBack()
    {
        var lu = _tuning.GetCoreClockOffsetMhz(_gpu);
        return lu.IsOk
            ? Result<bool>.Ok(lu.Value == _cible)
            : Result<bool>.Ok(false);   // illisible = non prouve, jamais « suppose bon ».
    }
}
