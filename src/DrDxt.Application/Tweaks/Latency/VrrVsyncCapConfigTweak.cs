using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// D3 — VrrVsyncCapConfigTweak.
//
// Configures the Blur-Busters low-latency combo at the DRIVER level for a VRR
// (G-Sync / FreeSync) display: Vsync ON + a frame cap ~3 fps under the refresh.
// This keeps the frame rate inside the VRR window with the lowest latency and no
// tearing. The per-game cap itself is often in-engine; this configures the
// DRIVER Vsync + driver cap and surfaces the exact value (refresh − 3) to the UI.
//
// Gating: a VRR display must be DETECTED (IDisplayInfo) — no blind cap on a
// non-VRR panel. Targets the primary game display; ambiguous → skip.
//
// 14/09/2026 (plan complet 1.3) : le combo s'ecrit au profil du JEU en cours, comme
// PreferMaxPerf — il ecrivait dans « Base Profile », donc pour tous les jeux et le bureau.
//
// Captures the original Vsync + frame-limit as a COMPOSITE inverse snapshot so
// revert restores both. Every external boundary wrapped → fail clean, never throws.
// -----------------------------------------------------------------------
public sealed class VrrVsyncCapConfigTweak : LatencyTweakBase
{
    // DRS setting ids (driver settings, not free registry keys). Vsync + frame limit.
    // Verified verbatim against NVIDIA's official NvApiDriverSettings.h
    // (github.com/NVIDIA/nvapi): VSYNCMODE_ID = 0x00A879CF, FRL_FPS_ID = 0x10835002.
    // The earlier M2 values (0x00fc00fa VSYNC, 0x10835000 FRL) were wrong and are superseded.
    private const uint VsyncModeSettingId = 0x00A879CF;        // VSYNCMODE_ID
    private const uint FrameRateLimiterSettingId = 0x10835002;  // FRL_FPS_ID
    // VSYNCMODE is an ENUM of magic constants, not a 0/1 flag (verified vs NvApiDriverSettings.h:
    // PASSIVE=0x60925292, FORCEON=0x47814940, FORCEOFF=0x08416747; a live RTX 4060 read the
    // default as PASSIVE=0x60925292). "Vsync ON" is FORCEON — writing 1 set an invalid value.
    private const uint VsyncOn = 0x47814940;                   // VSYNCMODE_FORCEON
    private const int CapBelowRefresh = 3;                      // Blur-Busters: refresh − 3
    // FRL_FPS documented range = 0-1023 fps (NvApiDriverSettings.h) — never write out of range.
    private const int FrlMaxFps = 1023;

    private readonly INvApi _nvApi;
    private readonly IDisplayInfo _display;
    private readonly IGameExecutable? _game;
    private readonly IAdlx? _adlx;

    /// <param name="game">Le jeu en cours. Absent (outils, tests) ou jeu sans profil lisible :
    /// ecriture globale, comme avant.</param>
    /// <param name="adlx">A2b (07/10/2026) : le pilote AMD. Sans NVAPI, le plafond passe par FRTC (ADLX), et la Vsync
    /// du pilote AMD (A2c, 08/10/2026) est mise « toujours activée », l'équivalent de FORCEON chez NVIDIA.</param>
    public VrrVsyncCapConfigTweak(INvApi nvApi, IDisplayInfo display, IElevatedClient elevated,
        IGameExecutable? game = null, IAdlx? adlx = null)
        : base(elevated)
    {
        _nvApi = nvApi;
        _display = display;
        _game = game;
        _adlx = adlx;
    }

    public override string Id => "latency.vrr_vsync_cap";
    public override string SummaryKey => "tweak.latency.vrr_vsync_cap.combo";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        if (!LatencyGating.FixeOuPortable(hw))
            return false;
        // Need a driver path (NVIDIA or AMD render GPU) AND a detected VRR display.
        var hasDriverPath = LatencyGating.HasNvidiaDriverPath(hw) || LatencyGating.HasAmdDriverPath(hw);
        if (!hasDriverPath)
            return false;

        var display = _display.GetPrimaryGameDisplay();
        if (!display.IsOk || display.Value is not { SupportsVrr: true } ecran)
            return false;
        if (LatencyGating.HasNvidiaDriverPath(hw))
            return true;

        // A2b : sur AMD, avant ce lot, le levier était proposé puis ne pouvait qu'échouer (NVAPI seul). Il n'est proposé
        // que si FRTC, Chill, la Vsync et Enhanced Sync sont lus, Chill et Enhanced Sync éteints (allumer la Vsync éteint
        // Enhanced Sync, et la rendre ne le rallume pas), et le combo pas déjà en place.
        return _adlx is { IsAvailable: true }
            && _adlx.GetChillActif() is { IsOk: true, Value: false }
            && _adlx.GetEnhancedSyncActif() is { IsOk: true, Value: false }
            && _adlx.GetFrameRateTarget() is { IsOk: true } frtc
            && _adlx.GetVsyncMode() is { IsOk: true } vsync
            && (frtc.Value != Cible(ecran.RefreshHz) || vsync.Value != VsyncAmdToujours);
    }

    /// <summary>ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE.WFVR_ALWAYS_ON (ADLXDefines.h).</summary>
    private const int VsyncAmdToujours = 3;

    /// <summary>Carte AMD sans NVAPI, mais FRTC ou Chill illisible : rien n'est proposé, et c'est dit.</summary>
    public override bool EtatNonLu(SettingsState state)
        => !_nvApi.IsAvailable && _adlx is { IsAvailable: true }
           && (!_adlx.GetFrameRateTarget().IsOk || !_adlx.GetChillActif().IsOk
               || !_adlx.GetVsyncMode().IsOk || !_adlx.GetEnhancedSyncActif().IsOk);

    private static int Cible(int rafraichissementHz) => Math.Clamp(rafraichissementHz - CapBelowRefresh, 1, FrlMaxFps);

    /// <summary>A2b/A2c : Vsync du pilote « toujours activée » puis FRTC à la cible, chacun seulement s'il n'y est pas
    /// déjà ; l'inverse (composite) rend le mode et l'état FRTC d'origine. Une seconde écriture refusée rend la première.
    /// GpuIndex 0 : pour ces réglages, le helper écrit la carte dédiée (celle que le lecteur lit), l'index est ignoré.</summary>
    private async Task<Result<TweakSnapshot>> ApplyAmdAsync(int cible)
    {
        if (_adlx!.GetChillActif() is not { IsOk: true, Value: false })
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Radeon Chill is on or unread: two driver frame limits would stack.");
        if (_adlx.GetEnhancedSyncActif() is not { IsOk: true, Value: false })
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Enhanced Sync is on or unread: the driver Vsync would switch it off for good.");
        var frtc = _adlx.GetFrameRateTarget();
        var vsync = _adlx.GetVsyncMode();
        if (!frtc.IsOk || !vsync.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not read the original FRTC state or Vsync mode.");

        var etapes = new List<(PrivilegedOp Pose, PrivilegedOp Inverse)>();
        if (vsync.Value != VsyncAmdToujours)
            etapes.Add((new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, VsyncAmdToujours),
                        new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, vsync.Value)));
        if (frtc.Value != cible)
            etapes.Add((new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, cible),
                        new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, frtc.Value)));
        if (etapes.Count == 0)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "The AMD driver already carries Vsync and the cap.");

        // Inverses dans l'ordre inverse des poses.
        var inverse = CompositeSnapshotCodec.Encode(etapes.Select(e => e.Inverse).Reverse().ToList());
        if (!inverse.IsOk || inverse.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

        var posees = new List<PrivilegedOp>();
        foreach (var (pose, inv) in etapes)
        {
            var r = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsOk)
            {
                return await RetourArriere.EchecAsync(Elevated, posees, r.Error!.Value.Code, r.Error!.Value.Message)
                    .ConfigureAwait(false);
            }
            posees.Add(inv);
        }
        // 08/10/2026 : les valeurs posées (Vsync, FRTC), pour que la désinstallation ne rende une feuille que si elle porte
        // encore ce que DrDXT y a mis.
        var poseesJson = CompositeSnapshotCodec.Encode(etapes.Select(e => e.Pose).ToList());
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, inverse.Value, poseesJson.IsOk ? poseesJson.Value : null));
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var display = _display.GetPrimaryGameDisplay();
            if (!display.IsOk || display.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not determine the primary game display.");

            // No blind cap: a non-VRR panel is out of bounds for this tweak.
            if (!display.Value.SupportsVrr)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                    "No VRR/G-Sync/FreeSync display detected: no blind frame cap.");

            if (!_nvApi.IsAvailable)
                return _adlx is { IsAvailable: true }
                    ? await ApplyAmdAsync(Cible(display.Value.RefreshHz)).ConfigureAwait(false)
                    : Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                        "Neither NVAPI nor ADLX is available to configure the frame cap.");

            var targetCap = (uint)Cible(display.Value.RefreshHz);

            // Capture the real originals before writing (faithful composite revert), at the scope written.
            var jeu = _game?.RunningGameExecutablePath();
            var vsync = PlanifierDrs(_nvApi, _game is not null, jeu, VsyncModeSettingId, VsyncOn, _nvApi.GetVsyncMode, "Vsync mode");
            if (!vsync.IsOk)
                return Result<TweakSnapshot>.Fail(vsync.Error!.Value.Code, vsync.Error.Value.Message);
            var cap = PlanifierDrs(_nvApi, _game is not null, jeu, FrameRateLimiterSettingId, targetCap, _nvApi.GetFrameRateLimit,
                "frame-rate limit");
            if (!cap.IsOk)
                return Result<TweakSnapshot>.Fail(cap.Error!.Value.Code, cap.Error.Value.Message);

            // 14/09/2026 — un profil de jeu lisible pour un reglage et pas pour l'autre poserait un
            // combo moitie au jeu, moitie au pilote entier. Les deux au meme endroit, ou rien.
            if (vsync.Value.Op.ProfileName != cap.Value.Op.ProfileName)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Vsync and the frame cap would not land in the same driver profile.");

            // Inverse ops (restore both originals). Order: cap then vsync (reverse of apply).
            var inverseOps = new PrivilegedOp[] { cap.Value.Inverse, vsync.Value.Inverse };
            var snapshotJson = CompositeSnapshotCodec.Encode(inverseOps);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            // Apply Vsync ON, then the frame cap. Each forward op is paired with the
            // inverse that restores this tweak's entry state. If the 2nd op fails, we
            // replay the inverse of the 1st (already-applied) op (LIFO) so nothing
            // stays half-applied (§6.2 "rien ne reste pendu") before returning Fail.
            var steps = new (PrivilegedOp Forward, PrivilegedOp Inverse)[]
            {
                (vsync.Value.Op, vsync.Value.Inverse),
                (cap.Value.Op, cap.Value.Inverse),
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

            // 11/09/2026 — les valeurs posees (Vsync, plafond), pour que la desinstallation ne rende un
            // reglage du pilote que s'il porte encore ce que DrDXT y a mis.
            var posees = CompositeSnapshotCodec.Encode(steps.Select(s => s.Forward).ToList());
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"VRR/Vsync cap apply boundary error: {ex.Message}");
        }
    }

    /// <summary>Composite revert: replay each captured-original op. Malformed
    /// snapshot → SAFETY.SNAPSHOT_INVALID; never throws, never runs an unknown op.</summary>
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        // Revue n° 9 : sur AMD, l'inverse est UNE op (FRTC), pas un composite. Lue comme composite, elle était
        // refusée à chaque remise : le plafond restait posé sur la carte. Une op seule passe par le chemin de la base.
        if (PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson) is { IsOk: true, Value: ApplyAdlxTuningOp })
            return await base.RevertAsync(snapshot).ConfigureAwait(false);

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

}
