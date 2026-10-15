using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledNvApiActions : une couture NVAPI à échec propre pour le réconciliateur de démarrage.
//
// Le réconciliateur de démarrage (InProcessElevatedClient, qui tourne en LocalSystem) doit être
// incapable par construction de toucher au GPU. Il existe une seule garde en amont (le catalogue
// et le mappeur réconciliables n'émettent jamais d'op GPU) : cette couture est donc la deuxième et
// dernière ligne. Même si un changement futur laissait passer une op GPU, le service de démarrage
// n'a aucune vraie implémentation NVAPI branchée et l'op échoue proprement au lieu d'écrire un
// décalage volatil au démarrage. La brancher évite aussi que le service de démarrage SYSTEM charge
// le moindre chemin de code NVAPI natif.
//
// OpAllowlist n'est pas une troisième garde. Validate() ne reçoit que l'op :
// ValidateNvApiPowerLimit accepte tout pourcentage 0..100 sur un index non négatif, et la même
// allowlist sert le helper déclenché par l'utilisateur, qui doit avoir le droit d'écrire. Elle
// borne la forme d'une op, jamais le contexte où elle tourne. Le refus au démarrage repose donc
// entièrement sur le branchement ci-dessous : c'est pourquoi un test l'épingle
// (HostCompositionTests, le jumeau GPU de celui du firmware), et pas de la prose.
//
// Raison : les décalages d'horloge / de puissance GPU sont volatils (effacés au redémarrage) et un
// décalage limite pourrait dégrader la stabilité du démarrage ; le réconciliateur ne fait persister
// que des réglages validés et durables (registre / plan d'alimentation / service), jamais de
// réglage GPU. Chaque méthode rend un Result.Fail propre et ne lève jamais (le contrat des deux
// coutures NVAPI).
// -----------------------------------------------------------------------
public sealed class DisabledNvApiActions : INvApiDrsActions, INvApiTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetGlobalSetting(uint settingId, uint value) => Fail();

    // Le reconciliateur de boot n'ecrit pas plus par jeu que globalement. Meme refus, meme
    // motif : un chemin qui refuse pour une raison differente serait un chemin de plus a auditer.
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
