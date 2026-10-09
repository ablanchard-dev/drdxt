using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — GameDvrBackgroundOffTweak (OS, persistent).
//
// Turns OFF Game DVR "background recording" (the always-on "Record what happened"
// buffer = the real overhead) via the per-user leaf
//   HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled = 0.
//
// SURGICAL (cahier §6, plan §0): only the background buffer is disabled. Manual capture
// (AppCaptureEnabled) and the whole Game Bar are deliberately left alone — we never sell
// "disable a feature" as FPS. Captures the real original first → serialized inverse → write 0.
//
// Takes effect after reboot / next game launch → the engine skips the immediate A/B
// (TakesEffectAfterReboot = true); the proof is the next in-game scan, never this session.
//
// HONESTY (§6): the SummaryKey says "less background overhead", never "+FPS".
//
// Gating: AppliesTo reads the same leaf the apply writes — already 0 ⇒ not applicable.
// -----------------------------------------------------------------------
public sealed class GameDvrBackgroundOffTweak : LatencyTweakBase
{
    private const uint Disabled = 0; // 0 = background recording off, 1 = on.
    private const string GameDvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string HistoricalCaptureName = "HistoricalCaptureEnabled";

    private readonly IRegistryReader _registry;

    public GameDvrBackgroundOffTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.gameDvrBackgroundOff";
    public override string SummaryKey => "tweak.latency.game_dvr.background_off";

    // The DVR re-reads the setting at the next game launch → engine defers its A/B proof.
    public override bool TakesEffectAfterReboot => true;

    // Revue du 12/09 (C1) : deja a 0 ⇒ rien a faire, hors du plan ; illisible ⇒ applicable, non lu
    // n'est pas « deja regle ».
    // 14/09/2026 (C1 bis) : ce commentaire disait « absent = defaut Windows (actif) », sans source.
    // C'est l'inverse : « Record what happened » est desactive par defaut, et une valeur absente vaut 0
    // (elevenforum.com/t/…record-what-happened-for-gaming-captures-in-windows-11.17900 ;
    // github.com/FunkyFr3sh/GameDVR_Config). Le poste de reference n'a pas la valeur : le levier y
    // restait au plan, « rien a tenter » n'etait jamais vrai et le client passait par l'achat pour rien.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        try
        {
            var read = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName);
            return !(read.IsOk && read.Value is null or Disabled);
        }
        catch (Exception) { return true; } // frontiere externe : non lu, jamais « deja regle »
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture the real original before writing (faithful revert).
            var read = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original HistoricalCaptureEnabled: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Disable background recording (=0) through the allowlisted typed op.
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
            // Every external boundary wrapped → fail clean, never throw.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Game DVR background-off apply boundary error: {ex.Message}");
        }
    }
}
