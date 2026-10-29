using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuPowerLimitTweak : la limite de puissance d'une carte NVIDIA (% du TDP, ApplyNvApiPowerLimitOp) ou AMD
// (ApplyAdlxTuningOp PowerLimit). Plus basse : des températures plus basses et des 1 % low plus stables, pour un coût
// quasi nul en images de pointe sur un PC déjà frais. Sur AMD, la plage lue tranche entre % absolu et décalage
// (LimitePuissanceAmd), et rien n'est proposé sans rapport validé pour l'architecture (GpuTuningCoverage.ValideesEnVie,
// dit par Levier.PasIci.PuissanceAmd). Jamais sous GpuSafeBounds.MinPowerLimitPct (des saccades, pas des gains).
// Invariants du levier de décalage : carte dédiée de bureau, bornes GpuSafeBounds, original en op inverse, op typée,
// échec propre. Pas de test de stabilité : baisser la puissance ne déstabilise pas une carte stable.
// -----------------------------------------------------------------------
/// <summary>Abaisse la limite de puissance de la carte graphique (en % du TDP, bornée par GpuSafeBounds). Proposé sur
/// un PC de bureau à carte NVIDIA dédiée, ou AMD dont l'architecture a un rapport validé (voir <see cref="AppliesTo"/>) ;
/// volatil, le pilote remet sa valeur au redémarrage.</summary>
public sealed class GpuPowerLimitTweak : GpuTweakBase
{
    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IGpuTuningTargetSource _targets;
    private readonly IReadOnlySet<GpuArchitecture> _validees;

    /// <param name="validees">Les architectures validées par un rapport ; <see cref="GpuTuningCoverage.ValideesEnVie"/> en vie.</param>
    public GpuPowerLimitTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets,
        IReadOnlySet<GpuArchitecture>? validees = null)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _targets = targets;
        _validees = validees ?? GpuTuningCoverage.ValideesEnVie;
    }

    // La cible en cours (un profil de puce a pu la remplacer, bornée), rebornée plus bas avant toute écriture.
    private GpuPowerLimitTarget Target => _targets.PowerLimit;

    public override string Id => "gpu.power_limit";
    public override string SummaryKey => "tweak.gpu.power_limit.efficiency";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        GpuGating.HasDesktopDiscreteGpu(hw)
        && (hw.Gpus.Any(g => GpuGating.IsNvidia(g) && GpuGating.IsTunableDiscreteGpu(g)) || LimitePuissanceAmd.AmdValidee(hw, _validees));

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Jamais sous GpuSafeBounds.MinPowerLimitPct jusqu'au helper.
            var safePct = GpuSafeBounds.ClampPowerLimitPct(Target.PowerLimitPct);

            if (_nvApi.IsAvailable)
                return await ApplyNvidiaAsync(safePct).ConfigureAwait(false);

            // Jamais de saut silencieux : sur AMD sans rapport validé, rien n'est écrit, et c'est dit.
            if (_adlx.IsAvailable && _targets.AmdEcritureValidee)
                return await ApplyAmdAsync(safePct).ConfigureAwait(false);
            if (_adlx.IsAvailable || _targets.IsAmdGpuPresent)
                return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                    "AMD power limit not written: no validation report for this card's architecture.");

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither NVAPI nor ADLX is available for GPU power-limit.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU power-limit apply boundary error: {ex.Message}");
        }
    }

    private async Task<Result<TweakSnapshot>> ApplyAmdAsync(int safePct)
    {
        if (_adlx.ResolveTunableGpuIndex() is not { IsOk: true } index)
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable, "No tunable AMD GPU found.");
        var gpu = index.Value;
        if (_adlx.GetPowerLimitRange(gpu) is not { IsOk: true } plage || _adlx.GetPowerLimitPct(gpu) is not { IsOk: true } capture)
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable, "Could not read the AMD power limit or its range.");
        // La forme de la plage dit l'unité ; forme inconnue, origine au-dessus de l'usine ou rien à baisser : rien d'écrit.
        if (LimitePuissanceAmd.Cible(plage.Value!, capture.Value, safePct) is not { } valeur)
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                $"AMD power limit not written: range [{plage.Value!.Min}, {plage.Value.Max}], current {capture.Value}.");

        var inverse = new ApplyAdlxTuningOp(gpu, AdlxTuningKind.PowerLimit, capture.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // La même sonde que NVIDIA : l'origine réécrite à l'identique puis relue, avant toute vraie écriture.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _adlx.GetPowerLimitPct(gpu), capture.Value, "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        var pose = new ApplyAdlxTuningOp(gpu, AdlxTuningKind.PowerLimit, valeur);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        if (_adlx.GetPowerLimitPct(gpu) is { IsOk: true } relu) pose = pose with { IntValue = relu.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }

    private async Task<Result<TweakSnapshot>> ApplyNvidiaAsync(int safePct)
    {
        var capture = _nvApi.GetPowerLimitPct(Target.GpuIndex);
        if (!capture.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original power-limit: {capture.Error?.Code}.");

        var inverse = new ApplyNvApiPowerLimitOp(Target.GpuIndex, capture.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) La sonde de capacité (GpuWriteProbe) avant toute vraie écriture ; échec → TWEAK.UNSUPPORTED_ON_THIS_GPU.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _nvApi.GetPowerLimitPct(Target.GpuIndex), capture.Value,
            "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        var pose = new ApplyNvApiPowerLimitOp(Target.GpuIndex, safePct);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // La valeur posée, relue (un pilote qui arrondit la ferait passer pour celle du joueur) : la désinstallation ne rend
        // la limite que si elle porte encore la sienne.
        if (_nvApi.GetPowerLimitPct(Target.GpuIndex) is { IsOk: true } reluNv) pose = pose with { PowerLimitPercent = reluNv.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }
}
