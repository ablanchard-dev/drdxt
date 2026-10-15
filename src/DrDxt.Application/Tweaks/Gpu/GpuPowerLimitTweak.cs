using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuPowerLimitTweak.
//
// Plafonne la limite de puissance de la carte (en % du TDP) par NVAPI (ApplyNvApiPowerLimitOp) ou par
// la commande de limite de puissance d'ADLX sur AMD. Une limite plus basse donne des températures plus
// basses et des 1 % low plus stables, pour un coût quasi nul en images de pointe sur un PC déjà frais.
// La limite est toujours bornée par GpuSafeBounds.MinPowerLimitPct : jamais sous le plancher (ce qui
// ferait des saccades, pas des gains).
//
// Mêmes invariants que le levier de décalage : carte dédiée de PC de bureau, bornes GpuSafeBounds,
// original relu puis rangé en op inverse dans le snapshot, application par l'op typée autorisée, retour
// par la base, échec propre. Pas de test de stabilité : baisser la puissance ne rend pas instable une
// carte stable, contrairement à un décalage de tension.
// -----------------------------------------------------------------------
/// <summary>Abaisse la limite de puissance de la carte graphique (en % du TDP, bornée par GpuSafeBounds). Proposé sur
/// un PC de bureau à carte dédiée ; volatil, le pilote remet sa valeur au redémarrage.</summary>
public sealed class GpuPowerLimitTweak : GpuTweakBase
{
    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IGpuTuningTargetSource _targets;

    public GpuPowerLimitTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _targets = targets;
    }

    // La cible de limite de puissance en cours. Un profil de puce a pu la remplacer au scan
    // (déjà bornée à l'entrée) ; relue ici et rebornée par GpuSafeBounds plus bas avant toute
    // écriture (défense en profondeur).
    private GpuPowerLimitTarget Target => _targets.PowerLimit;

    public override string Id => "gpu.power_limit";
    public override string SummaryKey => "tweak.gpu.power_limit.efficiency";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        GpuGating.HasDesktopDiscreteGpu(hw);

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Borner au plancher sûr : la limite de puissance n'atteint jamais le helper
            // sous GpuSafeBounds.MinPowerLimitPct.
            var safePct = GpuSafeBounds.ClampPowerLimitPct(Target.PowerLimitPct);

            if (_nvApi.IsAvailable)
                return await ApplyNvidiaAsync(safePct).ConfigureAwait(false);
            if (_adlx.IsAvailable)
                return await ApplyAmdAsync(safePct).ConfigureAwait(false);

            // Jamais de saut silencieux. Si un GPU AMD est présent (fait matériel tiré de l'inventaire,
            // indépendant du SDK ADLX) mais qu'aucun SDK de réglage n'est branché, on affiche un état
            // explicite et honnête.
            if (_targets.IsAmdGpuPresent)
                return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                    "AMD GPU detected: ADLX tuning SDK not available in this build.");

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither NVAPI nor ADLX is available for GPU power-limit.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU power-limit apply boundary error: {ex.Message}");
        }
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

        // (3b) Sonde de capacité : prouver que l'écriture marche sur cette carte et ce pilote
        // avant de toucher à quoi que ce soit. On rejoue l'op inverse (réécriture à l'identique de
        // la valeur relevée, aucun changement matériel) et on relit. Une carte dont le pilote dit
        // « réussi » sans rien écrire est prise ici, au lieu de produire un faux « appliqué ».
        // Échec → TWEAK.UNSUPPORTED_ON_THIS_GPU, rien n'est réglé.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _nvApi.GetPowerLimitPct(Target.GpuIndex), capture.Value,
            "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        var pose = new ApplyNvApiPowerLimitOp(Target.GpuIndex, safePct);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // La valeur posée, relue (un pilote qui arrondit ferait croire au rendu que le joueur
        // l'a changée), pour que la désinstallation rende la limite seulement si elle porte encore la sienne.
        if (_nvApi.GetPowerLimitPct(Target.GpuIndex) is { IsOk: true } reluNv) pose = pose with { PowerLimitPercent = reluNv.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }

    private async Task<Result<TweakSnapshot>> ApplyAmdAsync(int safePct)
    {
        // Trouver d'abord le GPU AMD dédié réglable (iGPU + dGPU : l'index 0 est souvent l'iGPU
        // non réglable). La couture de lecture suit le sélecteur du banc AMD et rend le premier
        // index dont le réglage GFX se lit. Échec propre (rien de posé) si aucun n'est trouvé.
        var resolved = _adlx.ResolveTunableGpuIndex();
        if (!resolved.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not resolve a tunable AMD GPU: {resolved.Error?.Code}.");
        int gpuIndex = resolved.Value;

        var capture = _adlx.GetPowerLimitPct(gpuIndex);
        if (!capture.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original power-limit: {capture.Error?.Code}.");

        var inverse = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.PowerLimit, capture.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) Sonde de capacité : prouver que l'écriture marche sur cette carte et ce pilote
        // avant de toucher à quoi que ce soit. On rejoue l'op inverse (réécriture à l'identique de
        // la valeur relevée, aucun changement matériel) et on relit. Une carte dont le pilote dit
        // « réussi » sans rien écrire est prise ici, au lieu de produire un faux « appliqué ».
        // Échec → TWEAK.UNSUPPORTED_ON_THIS_GPU, rien n'est réglé.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _adlx.GetPowerLimitPct(gpuIndex), capture.Value,
            "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        // Relevé, écriture et retour (par le snapshot) visent tous le même GPU physique trouvé.
        var pose = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.PowerLimit, safePct);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        if (_adlx.GetPowerLimitPct(gpuIndex) is { IsOk: true } reluAmd) pose = pose with { IntValue = reluAmd.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }
}
