using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// F1 — X3dCcdParkingFixTweak (the killer fix-it feature).
//
// Repairs WRONG CCD parking on a dual-CCD AMD X3D CPU (7950X3D etc.) so games run
// on the V-Cache CCD. 08/10/2026 (A6) : la seule mesure publique trouvée donne ~6 % en moyenne en 1080p
// (Hardware Busters, 02/04/2023 : sans parcage, 94,12 % de la machine bien réglée) ; le « deux chiffres »
// d'avant n'avait aucune source. L'écart réel est mesuré par le produit — but only
// via the supported software route (cahier §4 P2.3):
//   (1) start the AMD 3D V-Cache Performance Optimizer service (AMD3DVCacheSvc),
//   (2) enable Game Bar auto game mode (the optimizer keys off Game Bar).
// 24/09/2026 : l'ancienne étape « CppcEnable = 1 sous ...\Control\Processor » est retirée. Aucune
// source trouvée ne montre que Windows lise cette valeur ; CPPC se règle au BIOS. Un instantané
// ancien qui la porte s'annule toujours : la feuille reste dans l'allowlist pour ça.
// NO kernel hack, NO BIOS write. The BIOS CPPC option is GUIDED in M3, never
// scripted here.
//
// Capture the real original service + Game Bar state → a COMPOSITE
// inverse snapshot; revert restores both. Every external boundary wrapped →
// fail clean, never throws.
//
// HONESTY (§6): SummaryKey describes the repair, never "+FPS". The gain is PROVEN
// by the engine's re-bench (Phase C2); if no measured gain, no gain is shown.
//
// Detection false-positive risk: the heuristic is CONSERVATIVE (X3dParkingHeuristic)
// and the route is fully supported + fully reverted, so a false positive costs
// nothing measurable.
// -----------------------------------------------------------------------
public sealed class X3dCcdParkingFixTweak : FixItTweakBase
{
    private const string ServiceName = "AMD3DVCacheSvc";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameBarName = "AllowAutoGameMode";
    private const uint Enable = 1;

    private readonly IServiceStateReader _service;
    private readonly IRegistryReader _registry;

    public X3dCcdParkingFixTweak(
        IServiceStateReader service,
        IRegistryReader registry,
        IElevatedClient elevated)
        : base(elevated)
    {
        _service = service;
        _registry = registry;
    }

    public override string Id => "fixit.x3d_ccd";
    public override string SummaryKey => "tweak.fixit.x3d_ccd.repair_routing";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        // Dual-CCD X3D (single-CCD 7800X3D/9800X3D, Intel, non-X3D are excluded by
        // the heuristic) whose parking looks wrong.
        X3dParkingHeuristic.LooksMisparked(hw.Cpu, state);

    /// <summary>Revue n° 14 : le parking non lu n'est pas « rien n'est parqué ».</summary>
    public override bool EtatNonLu(SettingsState state) => state.ParkedCoreIndexes is null;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // (Capture) Read the real originals before writing, for a faithful revert.
            var serviceState = _service.GetServiceState(ServiceName);
            if (!serviceState.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read the AMD 3D V-Cache service state: {serviceState.Error?.Code}.");

            var gameBar = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName);
            if (!gameBar.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original Game Bar auto-mode: {gameBar.Error?.Code}.");

            // Build the COMPOSITE inverse snapshot (restore both originals).
            var inverseOps = new List<PrivilegedOp>
            {
                BuildRegistryInverse(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName, gameBar.Value),
            };
            // Only add a service inverse if the service was not already running — we
            // must not stop a service the user already had on.
            if (serviceState.Value == ServiceState.Stopped)
                inverseOps.Add(new RunServiceControlOp(ServiceName, ServiceAction.Stop));

            var snapshotJson = CompositeSnapshotCodec.Encode(inverseOps);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert ops.");

            // (Apply — supported route only) service start + Game Bar.
            // Each forward op is paired with the inverse that returns the system to
            // This tweak's entry state. If any op fails mid-sequence, we replay the
            // inverses of the ops already applied (LIFO) so nothing stays half-applied
            // (§6.2 "rien ne reste pendu") before returning the original Fail.
            var enableValue = Enable.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var steps = new (PrivilegedOp Forward, PrivilegedOp? Inverse)[]
            {
                (
                    new RunServiceControlOp(ServiceName, ServiceAction.Start),
                    // Only undo the start if the service was Stopped at entry; if it was
                    // already running, there is nothing to undo (never stop a service the
                    // user already had on) → null inverse, skipped on rollback.
                    serviceState.Value == ServiceState.Stopped
                        ? new RunServiceControlOp(ServiceName, ServiceAction.Stop)
                        : null
                ),
                (
                    new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName,
                        RegistryValueType.DWord, enableValue),
                    BuildRegistryInverse(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName, gameBar.Value)
                ),
            };

            var appliedInverses = new List<PrivilegedOp?>(steps.Length);
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

            // 11/09/2026 — les valeurs posees (service, Game Bar), pour que la desinstallation ne
            // rende une feuille que si elle porte encore ce que DrDXT y a mis.
            var posees = CompositeSnapshotCodec.Encode(steps.Select(s => s.Forward).ToList());
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"X3D CCD parking fix apply boundary error: {ex.Message}");
        }
    }

    /// <summary>Composite revert: replay each captured-original op. Malformed
    /// snapshot → SAFETY.SNAPSHOT_INVALID; never throws, never runs an unknown op.</summary>
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

    // The serialized inverse op for a single DWORD registry leaf: if the value
    // existed, restore it; if it was absent, the faithful inverse is a delete.
    private static PrivilegedOp BuildRegistryInverse(
        RegistryHiveKind hive, string path, string name, uint? capturedOriginal) =>
        capturedOriginal is uint original
            ? new SetRegistryValueOp(hive, path, name, RegistryValueType.DWord,
                original.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeleteRegistryValueOp(hive, path, name);
}
