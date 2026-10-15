using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledAdlxActions : une couture ADLX (AMD) à échec propre pour le réconciliateur de
// démarrage, le miroir AMD de DisabledNvApiActions.
//
// Le réconciliateur de démarrage (InProcessElevatedClient, qui tourne en LocalSystem) doit être
// incapable par construction de toucher au GPU, sur une machine AMD comme sur une NVIDIA. Il
// existe une seule garde en amont (le catalogue et le mappeur réconciliables n'émettent jamais
// d'op GPU) : cette couture est donc la deuxième et dernière ligne. Même si un changement futur
// laissait passer une op ADLX, le service de démarrage n'a aucune vraie implémentation ADLX
// branchée et l'op échoue proprement au lieu d'écrire un décalage volatil au démarrage. La
// brancher évite aussi que le service de démarrage SYSTEM charge le moindre chemin de code ADLX natif.
//
// OpAllowlist n'est pas une troisième garde : ValidateAdlx accepte tout ApplyAdlxTuningOp bien
// formé (index >= 0, Kind connu) et rend Ok. Elle borne la forme d'une op, jamais le contexte où
// elle tourne : la même allowlist sert le helper déclenché par l'utilisateur, qui doit avoir le
// droit d'écrire. Le refus au démarrage repose entièrement sur le branchement ci-dessous, et un
// test l'épingle, pas de la prose.
//
// Raison : les décalages d'horloge / de puissance GPU sont volatils (effacés au redémarrage) et un
// décalage limite pourrait dégrader la stabilité du démarrage ; le réconciliateur ne fait persister
// que des réglages validés et durables (registre / plan d'alimentation / service), jamais de
// réglage GPU. SetTuning rend toujours un Result.Fail propre et ne lève jamais (le contrat de
// toute couture GPU).
// -----------------------------------------------------------------------
public sealed class DisabledAdlxActions : IADLXTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, Message);
}
