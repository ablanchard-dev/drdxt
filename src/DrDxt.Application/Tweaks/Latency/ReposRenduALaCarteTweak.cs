using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// Le profil global du pilote en « performances maximales » garde la carte au maximum hors jeu :
// mesuré le 24/09 sur la machine de référence, 55,6 W et 2 490 MHz au repos. Ce levier rend
// « Normal » (PREFERRED_PSTATE_ADAPTIVE, voir PreferMaxPerfTweak.Normal) au profil global
// seulement : un maximum posé sur le profil d'un jeu reste le choix de ce jeu.
//
// L'original est relu au moment d'écrire (le scan peut dater) : s'il n'est plus au maximum, rien n'est
// écrit. L'annulation réécrit la valeur lue ; la cible voyage dans le snapshot pour la relecture. Ce
// levier ne reste que prouvé : la mesure du repos le retire sans baisse au-delà du bruit.
// -----------------------------------------------------------------------
/// <summary>Rend « Normal » au profil global NVIDIA, pour que la carte redescende au repos. Proposé sur un PC de
/// bureau à carte NVIDIA quand le global est lu en « performances maximales ».</summary>
public sealed class ReposRenduALaCarteTweak : LatencyTweakBase
{
    private readonly INvApi _nvApi;

    public ReposRenduALaCarteTweak(INvApi nvApi, IElevatedClient elevated) : base(elevated) => _nvApi = nvApi;

    public override string Id => "gpu.repos_global_normal";
    public override string SummaryKey => "tweak.gpu.repos_global_normal";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis == ChassisType.Desktop
        && LatencyGating.HasDesktopNvidiaRenderGpu(hw)
        && state.NvidiaPreferMaxPerf == TriState.On
        && state.NvidiaDrsScope is null;

    public override bool EtatNonLu(SettingsState state) => state.NvidiaPreferMaxPerf == TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            if (!_nvApi.IsAvailable)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI is not available.");

            var lu = _nvApi.GetPreferredPstate();
            if (!lu.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"The global power management mode could not be read: nothing is written ({lu.Error?.Code}).");
            if (lu.Value != PreferMaxPerfTweak.PreferMax)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                    $"The global power management mode reads {lu.Value}, not maximum performance: nothing to give back.");

            var inverse = PrivilegedOpCodec.Serialize(
                new ApplyNvApiDrsSettingOp(PreferMaxPerfTweak.PreferredPstateSettingId, lu.Value, ProfilDeBase));
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var op = new ApplyNvApiDrsSettingOp(PreferMaxPerfTweak.PreferredPstateSettingId, PreferMaxPerfTweak.Normal, ProfilDeBase);
            var ecrit = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            return ecrit.IsOk
                ? Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverse.Value, op))
                : Result<TweakSnapshot>.Fail(ecrit.Error!.Value.Code, ecrit.Error.Value.Message);
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Global power mode apply boundary error: {ex.Message}");
        }
    }
}
