using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledNvApiActions : une couture NVAPI à échec propre pour le réconciliateur de démarrage (InProcessElevatedClient,
// LocalSystem), incapable par construction de toucher au GPU. Première garde : le catalogue et le mappeur réconciliables
// n'émettent jamais d'op GPU ; celle-ci est la seconde et dernière : aucune vraie NVAPI branchée, aucun code NVAPI natif
// chargé par le service SYSTEM.
// OpAllowlist n'est pas une troisième garde : elle borne la forme d'une op (ValidateNvApiPowerLimit accepte 0..100 sur
// un index non négatif, pour le helper de l'utilisateur), jamais son contexte. Le refus repose sur ce branchement,
// épinglé par HostCompositionTests (le jumeau GPU de celui du firmware).
// Les décalages GPU sont volatils et un décalage limite pourrait dégrader le démarrage : le réconciliateur ne fait
// persister que registre, plan d'alimentation et service. Chaque méthode rend Result.Fail, jamais d'exception.
// -----------------------------------------------------------------------
public sealed class DisabledNvApiActions : INvApiDrsActions, INvApiTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetGlobalSetting(uint settingId, uint value) => Fail();

    // Pas plus par jeu que globalement : meme refus, meme motif, un seul chemin a auditer.
    public Result SetSettingForApplication(string applicationPath, uint settingId, uint value) => Fail();

    // Ni ecriture ni suppression par jeu au boot : meme refus, meme motif.
    public Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue) => Fail();

    public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => FailInt();

    public Result<int> GetPowerLimitPercent(int gpuIndex) => FailInt();

    public Result SetCoreClockOffset(int gpuIndex, int offsetMHz) => Fail();

    public Result SetPowerLimitPercent(int gpuIndex, float pct) => Fail();

    private static Result Fail() => Result.Fail(ErrorCodes.HelperOpFailed, Message);

    private static Result<int> FailInt() => Result<int>.Fail(ErrorCodes.HelperOpFailed, Message);
}
