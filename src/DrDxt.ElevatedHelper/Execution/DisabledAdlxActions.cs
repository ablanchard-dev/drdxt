using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DisabledAdlxActions : le miroir AMD de DisabledNvApiActions, mêmes deux gardes, même raison. Aucune vraie ADLX
// branchée au réconciliateur de démarrage (InProcessElevatedClient, LocalSystem), aucun code ADLX natif chargé.
// OpAllowlist n'est pas une troisième garde : ValidateAdlx accepte tout ApplyAdlxTuningOp bien formé (index >= 0, Kind
// connu), pour le helper de l'utilisateur ; le refus repose sur ce branchement, épinglé par un test. SetTuning rend
// toujours Result.Fail, jamais d'exception.
// -----------------------------------------------------------------------
public sealed class DisabledAdlxActions : IADLXTuningActions
{
    private const string Message =
        "GPU tuning is never performed by the boot reconciler (offsets are volatile and only " +
        "validated, persistent settings are reapplied).";

    public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, Message);
}
