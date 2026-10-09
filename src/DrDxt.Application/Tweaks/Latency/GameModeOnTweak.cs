using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — GameModeOnTweak (OS, persistent).
//
// Enables Windows Game Mode by setting both per-user GameBar leaves to 1:
//   * AllowAutoGameMode   — the allow gate (the one detection reads),
//   * AutoGameModeEnabled — the modern Settings toggle.
// Documented to be set together (the Settings UI / community guides write both); only
// flipping one can leave Game Mode effectively off, so we write both and revert both.
//
// Composite snapshot (mirrors X3dCcdParkingFixTweak): capture each real original before
// writing → CompositeSnapshotCodec → faithful revert of all leaves. A failure mid-apply
// rolls back the leaves already written (LIFO) so nothing stays half-applied.
//
// Takes effect after reboot / next game launch → the engine skips the immediate A/B
// (TakesEffectAfterReboot = true); the proof is the next in-game scan, never this session.
//
// HONESTY (§6): Game Mode's impact varies, so it is PROPOSED (when off), never auto-applied
// silently, and the SummaryKey describes the change (no "+FPS"). Persists across reboot.
// -----------------------------------------------------------------------
public sealed class GameModeOnTweak : LatencyTweakBase
{
    private const uint Enable = 1;
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string AllowName = "AllowAutoGameMode";
    private const string EnabledName = "AutoGameModeEnabled";

    private readonly IRegistryReader _registry;

    public GameModeOnTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.gameModeOn";
    public override string SummaryKey => "tweak.latency.game_mode.enable";

    // Game Mode applies on the next game launch / restart → engine defers its A/B proof.
    public override bool TakesEffectAfterReboot => true;

    // Proposed whenever Game Mode is not currently on (the keys are user prefs that may be
    // absent by default; writing them is safe and reversible).
    /// <summary>Uniquement quand Game Mode est lu comme desactive. `Unknown` (illisible) ne
    /// declenche rien : on ne repare pas ce qu'on n'a pas su lire.</summary>
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.GameMode == TriState.Off;

    // `AllowAutoGameMode` absent du registre => Unknown. Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) => state.GameMode == TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture the real originals before writing (faithful composite revert).
            var allow = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, AllowName);
            if (!allow.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original AllowAutoGameMode: {allow.Error?.Code}.");
            var enabled = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName);
            if (!enabled.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original AutoGameModeEnabled: {enabled.Error?.Code}.");

            var allowInverse = Inverse(AllowName, allow.Value);
            var enabledInverse = Inverse(EnabledName, enabled.Value);

            var snapshotJson = CompositeSnapshotCodec.Encode(new List<PrivilegedOp> { allowInverse, enabledInverse });
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            var value = Enable.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var steps = new (PrivilegedOp Forward, PrivilegedOp Inverse)[]
            {
                (new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, AllowName,
                    RegistryValueType.DWord, value), allowInverse),
                (new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName,
                    RegistryValueType.DWord, value), enabledInverse),
            };

            var appliedInverses = new List<PrivilegedOp>(steps.Length);
            foreach (var (forward, inverse) in steps)
            {
                var res = await Elevated.ExecuteAsync(forward, CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                {
                    return await RetourArriere.EchecAsync(Elevated, appliedInverses, res.Error!.Value.Code, res.Error!.Value.Message)
                        .ConfigureAwait(false);
                }
                appliedInverses.Add(inverse);
            }

            // 11/09/2026 — les valeurs posees, pour que la desinstallation ne rende une feuille que si elle
            // porte encore ce que DrDXT y a mis (un reglage change a la main depuis est respecte).
            var posees = CompositeSnapshotCodec.Encode(steps.Select(s => s.Forward).ToList());
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Game Mode enable apply boundary error: {ex.Message}");
        }
    }

    // Composite revert: replay each captured-original op. Malformed snapshot →
    // SAFETY.SNAPSHOT_INVALID; never throws, never runs an unknown op.
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<IReadOnlyList<PrivilegedOp>> decoded;
        try
        {
            decoded = CompositeSnapshotCodec.Decode(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known composite of typed ops.");

        try
        {
            foreach (var op in decoded.Value)
            {
                var res = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                    return res; // surface the first revert failure verbatim
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

    // The inverse op for a single GameBar DWORD leaf: restore the captured value, or delete
    // it if it was absent originally (faithful revert of an added value).
    private static PrivilegedOp Inverse(string name, uint? capturedOriginal) =>
        capturedOriginal is uint original
            ? new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, name, RegistryValueType.DWord,
                original.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, name);
}
