using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Le boost du processeur coupé, rendu au défaut de son propre plan.
//
// Microsoft Learn (OEM, « PERFBOOSTMODE ») : 0 = désactivé en P-states ACPI, en CPPC et en CPPC autonome ; 1 à 4 =
// activé. Un plan dont la valeur secteur vaut 0 alors que le défaut de sa personnalité ne vaut pas 0 a été réglé par
// quelqu'un (un script, un autre outil) : le processeur ne dépasse plus sa fréquence nominale.
//
// Ce qui n'est pas sourcé : l'effet en jeu. La page Windows Server de Microsoft dit que ce réglage « only applies to
// Non-HWP systems », la page OEM donne « Disabled » aussi en mode autonome : les deux se contredisent. D'où un levier
// mesuré en jeu (AutoHot), jamais un gain annoncé : sans effet, il est rendu.
//
// Ce qui est écrit : la seule valeur secteur de PERFBOOSTMODE du plan actif, jamais la valeur batterie, jamais une
// valeur hors 1 à 4, jamais un autre réglage du plan. L'original est relu juste avant d'écrire.
//
// Ouvert sur portable, puisque seule la valeur secteur est écrite (la batterie n'est jamais
// touchée) et que l'essai est mesuré en jeu puis rendu s'il ne gagne rien. Écarté : consoles portables, profils verrouillés. Intel et AMD : proposé aux deux (la table couvre les trois modes ;
// pas de variante par classe de cœurs sur un Intel hybride).
// -----------------------------------------------------------------------
/// <summary>Rend au boost du processeur le défaut de son plan quand la valeur secteur du plan actif est lue coupée (0)
/// contre un défaut documenté. Proposé sur PC de bureau et portable, jugé en jeu.</summary>
public sealed class ModeBoostTweak : FixItTweakBase
{
    private readonly IPowerPlanReader _power;

    public ModeBoostTweak(IPowerPlanReader power, IElevatedClient elevated) : base(elevated) => _power = power;

    public override string Id => "fixit.mode_boost";
    public override string SummaryKey => "tweak.fixit.mode_boost.summary";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis is ChassisType.Desktop or ChassisType.Laptop
        && state.ModeDeBoost is { } lu
        && Guid.TryParse(state.PowerPlanGuid, out var actif) && lu.Plan == actif
        && ARendre(lu) is not null;

    public override bool EtatNonLu(SettingsState state) => state.ModeDeBoost is null;

    /// <summary>La valeur à poser, ou null : seulement un boost coupé contre un défaut que Microsoft documente.</summary>
    internal static uint? ARendre(ModeDeBoostLu lu) =>
        lu.Valeur == 0 && lu.Defaut is >= 1 and <= ModeDeBoostLu.MaxDocumente ? lu.Defaut : null;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var plan = _power.GetActivePowerPlan();
            if (!plan.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not read the active power plan.");
            var lu = _power.LireModeDeBoost(plan.Value);
            if (!lu.IsOk || lu.Value is null || ARendre(lu.Value) is not { } cible)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Boost mode unread, or not disabled against its plan's default: nothing to write.");

            var inverse = PrivilegedOpCodec.Serialize(new SetProcessorBoostModeOp(plan.Value, lu.Value.Valeur));
            var op = new SetProcessorBoostModeOp(plan.Value, cible);
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
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Boost mode apply boundary error: {ex.Message}");
        }
    }
}
