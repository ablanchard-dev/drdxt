using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Le plan « Économie d'énergie » rendu au plan Équilibré.
//
// Ce plan règle le processeur pour consommer peu : pour jouer, c'est le mauvais sens. Équilibré est le plan par défaut
// de Windows, et le seul qui porte le curseur « mode d'alimentation » de Windows 11 (Microsoft Learn, « Customize the
// Windows performance power slider »). Aucun gain d'images n'est publié : rien n'est chiffré, la mesure du produit juge.
//
// Ce qui est écrit : le plan actif, relu juste avant, passé à Équilibré ; l'Annuler rend le plan Économie. PC de bureau
// seulement : sur un portable ou une console portable, l'économie peut être voulue pour la batterie.
// -----------------------------------------------------------------------
/// <summary>Passe du plan « Économie d'énergie » au plan Équilibré, sur un PC de bureau.</summary>
public sealed class PlanEconomieTweak : FixItTweakBase
{
    private readonly IPowerPlanReader _power;

    public PlanEconomieTweak(IPowerPlanReader power, IElevatedClient elevated) : base(elevated) => _power = power;

    public override string Id => "fixit.plan_economie";
    public override string SummaryKey => "tweak.fixit.plan_economie.summary";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis == ChassisType.Desktop
        && Guid.TryParse(state.PowerPlanGuid, out var plan) && plan == KnownPowerPlans.PowerSaver;

    public override bool EtatNonLu(SettingsState state) => string.IsNullOrEmpty(state.PowerPlanGuid);

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var plan = _power.GetActivePowerPlan();
            if (!plan.IsOk || plan.Value != KnownPowerPlans.PowerSaver)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Active power plan unread, or no longer Power saver: nothing to write.");

            var inverse = PrivilegedOpCodec.Serialize(new SetActivePowerPlanOp(KnownPowerPlans.PowerSaver));
            var op = new SetActivePowerPlanOp(KnownPowerPlans.Balanced);
            var pose = PrivilegedOpCodec.Serialize(op);
            if (!inverse.IsOk || inverse.Value is null || !pose.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            return applied.IsOk
                ? Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, inverse.Value, pose.Value))
                : Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Power saver plan apply boundary error: {ex.Message}");
        }
    }
}
