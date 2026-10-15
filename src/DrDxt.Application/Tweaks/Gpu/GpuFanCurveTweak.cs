using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuFanCurveTweak.
//
// Pose une courbe de ventilation de la carte (NVAPI ou ADLX) par l'op autorisée ApplyGpuFanCurveOp.
// Une courbe plus vive garde la carte plus fraîche en charge : des 1 % low plus stables pour un coût
// quasi nul en images de pointe. La courbe est toujours bornée par GpuSafeBounds : aucun point à haute
// température ne descend sous la vitesse minimale sûre (anti-surchauffe), et le helper revérifie la
// feuille exacte, en seconde défense.
//
// Mêmes invariants que les leviers de décalage et de limite de puissance : carte dédiée de PC de
// bureau, bornes GpuSafeBounds, courbe d'origine relue puis rangée en op inverse dans le snapshot,
// application par l'op typée, retour par la base, échec propre.
// -----------------------------------------------------------------------
/// <summary>Pose une courbe de ventilation plus vive sur la carte graphique. Jamais proposé tant que le helper ne
/// sait pas écrire de courbe.</summary>
public sealed class GpuFanCurveTweak : GpuTweakBase
{
    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IGpuTuningTargetSource _targets;
    private readonly GpuFanCurveTarget _target;

    public GpuFanCurveTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets,
        GpuFanCurveTarget target)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _targets = targets;
        _target = target;
    }

    public override string Id => "gpu.fan_curve";
    public override string SummaryKey => "tweak.gpu.fan_curve.cooler";

    // Jamais proposé tant que le helper n'écrit pas de courbe. Son exécuteur rend
    // toujours « not yet available » (`PrivilegedOpExecutor.FanCurveDeferred`) : proposer ce levier
    // promettrait un réglage qui échoue à chaque essai.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) => false;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Lire la courbe d'origine par le SDK présent (pour un retour arrière fidèle).
            Result<FanCurve> capture;
            if (_nvApi.IsAvailable)
                capture = _nvApi.GetFanCurve(_target.GpuIndex);
            else if (_adlx.IsAvailable)
                capture = _adlx.GetFanCurve(_target.GpuIndex);
            else
            {
                // Jamais de saut silencieux. Si un GPU AMD est présent (fait matériel tiré de l'inventaire,
                // indépendant du SDK ADLX) mais qu'aucun SDK de réglage n'est branché, on affiche un état
                // explicite et honnête.
                if (_targets.IsAmdGpuPresent)
                    return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                        "AMD GPU detected: ADLX tuning SDK not available in this build.");

                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Neither NVAPI nor ADLX is available for GPU fan-curve.");
            }

            if (!capture.IsOk || capture.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original fan curve: {capture.Error?.Code}.");

            // Borner la courbe demandée pour qu'aucun point chaud ne soit silencieux (anti-surchauffe).
            var safeCurve = GpuSafeBounds.ClampFanCurve(_target.Curve);

            var inverse = ToOp(_target.GpuIndex, capture.Value);
            var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await Elevated
                .ExecuteAsync(ToOp(_target.GpuIndex, safeCurve), CancellationToken.None)
                .ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU fan-curve apply boundary error: {ex.Message}");
        }
    }

    private static ApplyGpuFanCurveOp ToOp(int gpuIndex, FanCurve curve) =>
        new(gpuIndex, curve.Points.Select(p => new FanCurvePointDto(p.TempC, p.DutyPct)).ToArray());
}
