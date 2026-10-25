using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// GameDvrBackgroundOffTweak (système, persistant).
//
// Coupe l'enregistrement en arrière-plan de Game DVR (le tampon « Enregistrer ce qui s'est passé »,
// toujours actif, qui est le vrai coût) par la feuille de l'utilisateur
//   HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled = 0.
//
// Chirurgical : seul le tampon d'arrière-plan est coupé. La capture manuelle (AppCaptureEnabled) et la
// Game Bar restent intactes : couper une fonction ne se vend pas comme des images. L'original est relu
// d'abord, l'inverse sérialisé, puis 0 est écrit.
//
// Effet au redémarrage ou au prochain lancement du jeu : le moteur saute l'A/B immédiat
// (TakesEffectAfterReboot = true) ; la preuve est le prochain scan en jeu, jamais cette session.
//
// Le SummaryKey dit « moins de charge en arrière-plan », jamais « +FPS ».
//
// Gating : AppliesTo lit la feuille même que l'application écrit ; déjà à 0, rien n'est proposé.
// -----------------------------------------------------------------------
/// <summary>Coupe l'enregistrement en arrière-plan de Game DVR (HistoricalCaptureEnabled = 0), proposé tant qu'il
/// n'est pas lu déjà coupé. La capture manuelle et la Game Bar restent.</summary>
public sealed class GameDvrBackgroundOffTweak : LatencyTweakBase
{
    private const uint Disabled = 0; // 0 = enregistrement en arrière-plan coupé, 1 = actif.
    private const string GameDvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string HistoricalCaptureName = "HistoricalCaptureEnabled";

    private readonly IRegistryReader _registry;

    public GameDvrBackgroundOffTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.gameDvrBackgroundOff";
    public override string SummaryKey => "tweak.latency.game_dvr.background_off";

    // Le DVR relit le réglage au prochain lancement du jeu → le moteur diffère sa preuve A/B.
    public override bool TakesEffectAfterReboot => true;

    // Deja a 0 ⇒ rien a faire, hors du plan ; illisible ⇒ non lu (EtatNonLu) : ni « deja regle », ni « a poser »,
    // puisque la pose relit l'origine et echouerait.
    // « Record what happened » est desactive par defaut, et une valeur absente vaut 0
    // (elevenforum.com/t/…record-what-happened-for-gaming-captures-in-windows-11.17900 ;
    // github.com/FunkyFr3sh/GameDVR_Config). Le poste de reference n'a pas la valeur : sans cette regle,
    // le levier y resterait au plan, « rien a tenter » ne serait jamais vrai et le client paierait pour rien.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        try
        {
            var read = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName);
            return read.IsOk && read.Value is not (null or Disabled);
        }
        catch (Exception) { return false; } // frontiere externe : non lu, dit par EtatNonLu
    }

    public override bool EtatNonLu(SettingsState state)
    {
        try { return !_registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName).IsOk; }
        catch (Exception) { return true; }
    }

    // Banc profond : déjà à 0 ou absente (absente vaut 0), l'exercice passe par 1 (l'enregistrement en arrière-plan
    // allumé) puis rend l'origine exacte, 0 ou absente.
    public override ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state)
    {
        try
        {
            if (_registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName) is not { IsOk: true, Value: null or Disabled } lu)
                return null;
            static string Op(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameDvrPath,
                HistoricalCaptureName, RegistryValueType.DWord, v)).Value!;
            var origine = BuildRegistryInverse(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName, lu.Value);
            return origine.IsOk && origine.Value is { } o ? new ExerciceProfond(o, Op("1")) : null;
        }
        catch (Exception) { return null; }
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Relever la vraie origine avant d'écrire (retour arrière fidèle).
            var read = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original HistoricalCaptureEnabled: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Couper l'enregistrement en arrière-plan (=0) par l'op typée sur liste blanche.
            var op = new SetRegistryValueOp(
                RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName,
                RegistryValueType.DWord,
                Disabled.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverse.Value, op));
        }
        catch (Exception ex)
        {
            // Chaque frontière externe protégée → échec propre, jamais d'exception.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Game DVR background-off apply boundary error: {ex.Message}");
        }
    }
}
