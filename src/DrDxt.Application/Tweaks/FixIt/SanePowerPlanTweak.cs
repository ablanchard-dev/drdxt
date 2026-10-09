using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// F3 — SanePowerPlanTweak : la politique d'alimentation du processeur (id `fixit.power_plan`,
// conserve : c'est une cle de persistance lue par le reconciliateur et les manifestes).
//
// REDEFINI LE 10/09/2026 (phase 18 F3, audit externe du 10/09). Ce levier se justifiait par le
// « core-parking » du plan Equilibre. Mesure sur la machine de reference (`powercfg /qh`) : la
// politique de parking est identique entre Equilibre et Haute performance (CPMINCORES,
// CPMAXCORES, CPCONCURRENCY... egaux). Ce que Haute performance change vraiment : preference
// energie/perf (EPP) 33 → 5, etat minimal du processeur 5 → 100 %, politique de boost 60 → 100,
// sous HWP autonome. Et 16 cœurs parques AU REPOS sont le comportement attendu d'un CPU hybride.
//
// Donc : le gate lit le GOULOT mesure par le scan (le processeur limite-t-il ce jeu ?), jamais
// le parking ; et le texte dit ce que ca fait, quand ca aide, et ce que ca coute — jamais
// « parking », jamais « micro-stutter », jamais « +FPS ».
//
// Gating (§16): desktop only. On a laptop, Balanced may be the user's intent for
// battery life → skip (do not apply). Handhelds have locked power profiles → skip.
//
// Apply: capture the real active plan GUID (power-read seam) → store as a
// serialized inverse SetActivePowerPlanOp in the snapshot → switch to High
// Performance via the helper. Any unavailable API → TWEAK.SDK_UNAVAILABLE
// (fail clean, no throw). Revert (base) restores the captured original plan.
// -----------------------------------------------------------------------
public sealed class SanePowerPlanTweak : FixItTweakBase
{
    private readonly IPowerPlanReader _power;

    public SanePowerPlanTweak(IPowerPlanReader power, IElevatedClient elevated)
        : base(elevated)
    {
        _power = power;
    }

    public override string Id => "fixit.power_plan";
    public override string SummaryKey => "tweak.cpu.power_policy.raise_perf_preference";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        // Desktop only — on a laptop Balanced may be wanted for battery (§16);
        // handhelds have locked power profiles.
        if (hw.Chassis != ChassisType.Desktop)
            return false;

        // X3D-PLAN (09/10/2026) : sur un Ryzen X3D à deux blocs, le pilote de chipset AMD (3D V-Cache Performance Optimizer)
        // range le jeu sur le bloc à cache et parque l'autre, et ne le fait que sous le plan Équilibré. Constat convergent de
        // la communauté AMD et des testeurs ; AMD n'en publie pas de note technique que nous ayons trouvée : dit comme tel.
        // Hautes performances casserait ce rangement. Le routage est l'affaire de fixit.x3d_ccd.
        if (hw.Cpu.IsX3D && hw.Cpu.CcdCount >= 2)
            return false;

        // Only when currently on Balanced (nothing to fix if already High Perf / Ultimate).
        if (!IsBalanced(state.PowerPlanGuid))
            return false;

        // PHASE 18 F3 — le processeur limite-t-il ce jeu ? Seule la mesure du scan le dit.
        // GPU-bound (97 % d'occupation le 08/09 sur la machine de reference) : monter la preference
        // du CPU ne rendrait rien et couterait du courant au repos. null (aucune mesure en jeu) ou
        // verdict inconnu : rien. Le parking n'entre plus dans la decision — il est identique entre
        // les deux plans (mesure du 10/09), et au repos il est attendu sur un CPU hybride.
        return state.GoulotMesure is BottleneckState.CpuBoundStrong
            or BottleneckState.CpuBoundOrAppBound
            or BottleneckState.MixedGpuLeaning;
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture the real active plan before switching (faithful revert).
            var active = _power.GetActivePowerPlan();
            if (!active.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read the active power plan: {active.Error?.Code}.");

            var inverse = new SetActivePowerPlanOp(active.Value);
            var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Switch to High Performance (a known, allowlisted plan GUID).
            var op = new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance);
            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            // La cible voyage avec le snapshot : sans elle, la relecture ne peut pas prouver que
            // ce plan-la est bien devenu actif, et aucun gain ne peut lui etre attribue.
            var cible = PrivilegedOpCodec.Serialize(op);
            return Result<TweakSnapshot>.Ok(
                new TweakSnapshot(Id, snapshotJson.Value, cible.IsOk ? cible.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Sane power-plan apply boundary error: {ex.Message}");
        }
    }

    // The plan is "Balanced" if its GUID parses to the known Balanced plan GUID.
    /// <summary>Revue n° 14 : le plan non lu, ou Équilibré sans mesure en jeu (le goulot décide), n'est pas « déjà bon ».</summary>
    public override bool EtatNonLu(SettingsState state)
        => string.IsNullOrEmpty(state.PowerPlanGuid) || (IsBalanced(state.PowerPlanGuid) && state.GoulotMesure is null);

    private static bool IsBalanced(string powerPlanGuid) =>
        Guid.TryParse(powerPlanGuid, out var guid) && guid == KnownPowerPlans.Balanced;
}
