using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// B3 (10/10/2026, boucle « allonger le bras ») : la carte sans fil sortie de son mode d'économie d'énergie quand le jeu
// passe par le Wi-Fi.
//
// Ce qui est sourcé : Microsoft Learn (Wi-Fi power management) : « Power save mode reduces power consumption at the cost
// of increased data transfer latency » ; attendu actif sur batterie « sauf quand une connexion à faible latence est
// requise ». Microsoft Q&A : des pics de ping en Wi-Fi réglés par « Performances maximales ». Le réglage vit dans le plan
// d'alimentation (sous-groupe « Paramètres de la carte sans fil », « Mode d'économie d'énergie », 0 = performances
// maximales, 1 à 3 = économies croissantes), secteur et batterie séparés.
//
// Ce qui est écrit : la seule valeur SECTEUR de ce réglage, dans le plan actif, à 0 ; jamais la valeur batterie (le
// choix du joueur quand il n'est pas branché), jamais un autre réglage du plan. L'original est relu juste avant.
// Proposé seulement quand un adaptateur sans fil est connecté et aucun filaire (sinon le jeu passe par le câble), et que
// la valeur lue n'est pas déjà « performances maximales » (sur secteur, le plan Équilibré y est déjà : la portée est un
// plan ou un constructeur qui l'a changé). Confort de latence : posé et relu, pas un gain d'images ; le scan suivant
// montre la gigue vers la box. Le vrai remède du Wi-Fi reste le câble, et le docteur le dit.
// -----------------------------------------------------------------------
public sealed class EconomieSansFilTweak : FixItTweakBase
{
    private readonly IPowerPlanReader _power;

    public EconomieSansFilTweak(IPowerPlanReader power, IElevatedClient elevated) : base(elevated) => _power = power;

    public override string Id => "fixit.wifi_performances";
    public override string SummaryKey => "tweak.fixit.wifi_performances.summary";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis is ChassisType.Desktop or ChassisType.Laptop
        && JoueEnSansFil(state)
        && state.EconomieSansFil is { } lu
        && Guid.TryParse(state.PowerPlanGuid, out var actif) && lu.Plan == actif
        && lu.Valeur != EconomieSansFilLue.PerformancesMaximales;

    public override bool EtatNonLu(SettingsState state) => state.EconomieSansFil is null || state.NetworkAdapters is null;

    /// <summary>Un adaptateur sans fil connecté et aucun filaire : c'est le Wi-Fi qui porte le jeu.</summary>
    internal static bool JoueEnSansFil(SettingsState state) =>
        state.NetworkAdapters is { Count: > 0 } cartes && cartes.Any(c => !c.IsWired) && !cartes.Any(c => c.IsWired);

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var plan = _power.GetActivePowerPlan();
            if (!plan.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not read the active power plan.");
            var lu = _power.LireEconomieSansFil(plan.Value);
            if (!lu.IsOk || lu.Value is null || lu.Value.Valeur == EconomieSansFilLue.PerformancesMaximales)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Wireless power saving mode unread, or already at maximum performance: nothing to write.");

            var inverse = PrivilegedOpCodec.Serialize(new SetWirelessPowerSavingOp(plan.Value, lu.Value.Valeur));
            var op = new SetWirelessPowerSavingOp(plan.Value, EconomieSansFilLue.PerformancesMaximales);
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
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Wireless power saving apply boundary error: {ex.Message}");
        }
    }
}
