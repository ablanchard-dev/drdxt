using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — VrrFlipModelOnTweak (OS, persistent).
//
// Turns ON, in one shot, the two Windows graphics toggles that share a single REG_SZ:
//   * VRROptimizeEnable     — "Variable refresh rate" (helps windowed/borderless VRR),
//   * SwapEffectUpgradeEnable — "Optimizations for windowed games" (flip-model presentation).
// Noms vérifiés le 24/09/2026 : ce sont ceux qu'écrivent les Paramètres de Windows eux-mêmes
// (lus tels quels dans le registre de la machine de référence, `SwapEffectUpgradeEnable=1;`).
// Both live in HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings, a
// "key=value;..." string also holding AutoHDREnable / HighPerfAdapter. So we MERGE: read the
// current string, set only our two keys, re-serialize, PRESERVING every other key (audit M2 —
// never clobber AutoHDR). The snapshot stores the RAW original string for a faithful revert
// (or a delete when the value was absent).
//
// Takes effect after reboot / next game launch → engine skips the immediate A/B (proof deferred).
//
// HONESTY (§6): PROPOSED, not auto-silent; SummaryKey describes the change, no "+FPS". VRR is a
// no-op on a non-VRR display (harmless); the flip-model half helps windowed games regardless.
//
// Gating: AppliesTo reads this DirectXUserGlobalSettings string itself (the scan's VRR reading comes
// from the driver, a different source). Flip-model absent ⇒ applicable on any display; flip-model
// already on ⇒ applicable only for the VRR half, and only on a display read as VRR-capable
// (`VrrCapableDisplay`, « C1 bis » du 14/09/2026, voir AppliesTo).
// -----------------------------------------------------------------------
public sealed class VrrFlipModelOnTweak : LatencyTweakBase
{
    private const string PrefsPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ValueName = "DirectXUserGlobalSettings";
    private static readonly (string Key, string Value)[] TurnOn =
    {
        ("VRROptimizeEnable", "1"),
        ("SwapEffectUpgradeEnable", "1"),
    };

    private readonly IRegistryReader _registry;

    public VrrFlipModelOnTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.vrrFlipModelOn";
    public override string SummaryKey => "tweak.latency.vrr_flipmodel.enable";

    public override bool TakesEffectAfterReboot => true;

    // Revue du 12/09 (C1) : les deux interrupteurs deja a 1 ⇒ rien a faire, hors du plan (sinon
    // « rien a tenter » n'est jamais vrai et le client paie pour rien). Illisible ⇒ applicable :
    // non lu n'est pas « deja regle ». La capacite VRR de l'ecran reste un suivi NS-8.
    //
    // 14/09/2026 (C1 bis) : la moitie VRR ne vaut rien sur un ecran qui ne fait pas de VRR. Le
    // poste de reference a deja le flip-model (`SwapEffectUpgradeEnable=1`) et pas `VRROptimizeEnable` :
    // le levier restait au plan pour la seule moitie VRR, que l'ecran soit capable ou non. Elle ne
    // compte plus que sur un ecran LU comme VRR (`VrrCapableDisplay`, deja lu par le scan).
    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        try
        {
            var read = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
            if (!read.IsOk)
                return true;
            if (!DirectXUserGlobalSettings.AlreadyHas(read.Value, [TurnOn[1]]))
                return true; // flip-model absent : utile a tout jeu fenetre, quel que soit l'ecran
            return !DirectXUserGlobalSettings.AlreadyHas(read.Value, [TurnOn[0]])
                && state.VrrCapableDisplay == TriState.On;
        }
        catch (Exception) { return true; } // frontiere externe : non lu, jamais « deja regle »
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture the real original string before writing (faithful revert of the whole value).
            var read = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original DirectXUserGlobalSettings: {read.Error?.Code}.");

            var original = read.Value; // null = the value was absent

            // Inverse: restore the exact original string, or delete the value if it was absent.
            PrivilegedOp inverse = original is null
                ? new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
                : new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName,
                    RegistryValueType.String, original);
            var inverseWire = PrivilegedOpCodec.Serialize(inverse);
            if (!inverseWire.IsOk || inverseWire.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Merge our two keys into the existing string (AutoHDR et al. preserved), write it back.
            var merged = DirectXUserGlobalSettings.Merge(original, TurnOn);
            var op = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName,
                RegistryValueType.String, merged);

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            // 06/10/2026 (plan 4.3) : la chaîne posée voyage avec l'inverse. Sans elle, la désinstallation ne
            // sait pas si la valeur est encore celle de DrDXT, et n'y touche pas.
            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverseWire.Value, op));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"VRR/flip-model apply boundary error: {ex.Message}");
        }
    }
}
