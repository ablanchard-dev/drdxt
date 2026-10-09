using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — HagsOnTweak (OS/latency, persistent).
//
// Enables Hardware-Accelerated GPU Scheduling (HAGS) via the allowlisted
// ...\GraphicsDrivers\HwSchMode registry leaf (DWORD 2 = On, 1 = Off — mirrors
// RegistryProvider.GetHags ; valeurs vérifiées le 24/09/2026 : learn.microsoft.com/answers
// question 4137979 et howtogeek.com/756935 ; sans support du pilote, Windows l'ignore), written through the elevated helper. Capture the real
// original first → store the inverse PrivilegedOp in the snapshot → write 2. Persists
// natively across reboot (registry), and is reconcilable.
//
// Takes effect after reboot: HwSchMode only applies after a restart, so the engine
// skips the immediate A/B for it (TakesEffectAfterReboot = true) — the gain is proven
// by the next in-game scan, never claimed for the current session.
//
// HONESTY (§6): HAGS is PROPOSED, never auto-applied silently; its real-world impact
// varies by GPU/driver/title, so the SummaryKey describes the change (never "+FPS")
// and the proof is the measured next-session A/B.
//
// Gating: only when HAGS is readable AND OFF — a present HwSchMode means the driver
// supports HAGS; Unknown (key absent) → not offered (can't confirm support). Vendor-
// agnostic (AMD / Intel / NVIDIA, WDDM 2.7+).
// -----------------------------------------------------------------------
public sealed class HagsOnTweak : LatencyTweakBase
{
    private const uint HagsOn = 2; // 2 = On, 1 = Off (HwSchMode).
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HwSchModeName = "HwSchMode";

    private readonly IRegistryReader _registry;

    public HagsOnTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.hagsOn";
    public override string SummaryKey => "tweak.latency.hags.enable";

    // Corrige LE 10/09/2026 (phase 18, P0.3). Ce gate disait `state.Hags != TriState.On`,
    // c'est-a-dire qu'il proposait aussi sur **Unknown**, en assumant qu'un `HwSchMode` Absent
    // vaut « HAGS eteint ». C'est faux : la cle absente veut dire que personne n'a ecrit ce
    // reglage, pas que l'ordonnancement materiel est inactif — Windows choisit alors son propre
    // defaut, qui depend de la version, du GPU et du pilote. Sur la machine de reference, la cle
    // est absente et l'etat effectif est INCONNU.
    //
    // Un candidat fonde sur une absence de cle est un candidat fonde sur rien : il ecrirait un
    // reglage pour « corriger » un etat que personne n'a lu, et l'A/B qui suit mesurerait un
    // changement dont on ne connait meme pas le point de depart.
    //
    // PHASE 18 F2 (10/09/2026) : cette lecture existe. Le gate lit l'etat effectif par D3DKMT
    // (`SettingsState.HagsEffective`), et la cle ne sert plus qu'a l'ecriture et au revert. Sur la
    // machine de reference, effectif = On ⇒ aucun candidat : c'est « deja bien regle », pas un trou.
    // Unknown (non lu, non supporte) ne se corrige toujours pas.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.HagsEffective == TriState.Off;

    // `HwSchMode` absent du registre => Unknown. Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) => state.HagsEffective == TriState.Unknown;

    // HwSchMode applies only after a restart → engine skips the immediate A/B (proof = next scan).
    public override bool TakesEffectAfterReboot => true;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture the real original before writing (faithful revert).
            var read = _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original HwSchMode: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Apply HAGS On (=2) through the allowlisted typed op.
            var op = new SetRegistryValueOp(
                RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName,
                RegistryValueType.DWord,
                HagsOn.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverse.Value, op));
        }
        catch (Exception ex)
        {
            // Every external boundary wrapped → fail clean, never throw.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"HAGS enable apply boundary error: {ex.Message}");
        }
    }
}
