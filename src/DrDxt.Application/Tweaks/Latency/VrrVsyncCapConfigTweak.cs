using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// VrrVsyncCapConfigTweak.
//
// Pose dans le pilote le combo basse latence de Blur Busters pour un écran VRR (G-Sync, FreeSync) :
// Vsync activée et un plafond d'images environ 3 images/s sous la fréquence de l'écran. Les images
// restent dans la fenêtre VRR, avec la latence la plus basse et sans déchirure. Le plafond par jeu est
// souvent dans le moteur ; ici, c'est la Vsync et le plafond du pilote, et l'écran affiche la valeur
// exacte (fréquence − 3).
//
// Gating : un écran VRR doit être détecté (IDisplayInfo), jamais de plafond à l'aveugle sur un écran
// sans VRR. Vise l'écran de jeu principal ; ambigu, rien n'est proposé.
//
// Le combo s'écrit au profil du jeu en cours, comme PreferMaxPerf : « Base Profile » toucherait tous
// les jeux et le bureau.
//
// La Vsync et le plafond d'origine sont gardés dans un snapshot composite : l'annulation rend les deux.
// Chaque frontière externe est protégée : échec propre, jamais d'exception.
// -----------------------------------------------------------------------
/// <summary>Pose Vsync et plafond d'images (fréquence − 3) dans le pilote, au profil du jeu. Proposé quand l'écran de jeu
/// est lu VRR, sur NVIDIA, ou sur AMD si Chill et Enhanced Sync sont lus éteints et le combo pas déjà en place.</summary>
public sealed class VrrVsyncCapConfigTweak : LatencyTweakBase
{
    // Ids des réglages DRS (réglages du pilote, pas des clés de registre libres). Vsync + limite d'images.
    // Vérifiés mot pour mot contre le NvApiDriverSettings.h officiel de NVIDIA
    // (github.com/NVIDIA/nvapi) : VSYNCMODE_ID = 0x00A879CF, FRL_FPS_ID = 0x10835002.
    // Les valeurs 0x00fc00fa (VSYNC) et 0x10835000 (FRL) sont fausses : ne pas les reprendre.
    private const uint VsyncModeSettingId = 0x00A879CF;        // VSYNCMODE_ID
    private const uint FrameRateLimiterSettingId = 0x10835002;  // FRL_FPS_ID
    // VSYNCMODE est une énumération de constantes magiques, pas un drapeau 0/1 (vérifié contre
    // NvApiDriverSettings.h : PASSIVE=0x60925292, FORCEON=0x47814940, FORCEOFF=0x08416747 ; une
    // RTX 4060 réelle lit le défaut PASSIVE=0x60925292). « Vsync ON » est FORCEON ; écrire 1 pose une valeur invalide.
    private const uint VsyncOn = 0x47814940;                   // VSYNCMODE_FORCEON
    // La cible : PlafondVrr (Core), la plus basse de « fréquence − 3 » et de la règle de Reflex.
    // Plage documentée de FRL_FPS = 0-1023 fps (NvApiDriverSettings.h) : jamais d'écriture hors plage.
    private const int FrlMaxFps = 1023;

    private readonly INvApi _nvApi;
    private readonly IDisplayInfo _display;
    private readonly IGameExecutable? _game;
    private readonly IAdlx? _adlx;

    /// <param name="game">Le jeu en cours. Absent (outils, tests) ou jeu sans profil lisible :
    /// ecriture globale, comme avant.</param>
    /// <param name="adlx">A2b : le pilote AMD. Sans NVAPI, le plafond passe par FRTC (ADLX), et la Vsync
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
        // Il faut un chemin pilote (GPU de rendu NVIDIA ou AMD) et un écran VRR détecté.
        var hasDriverPath = LatencyGating.HasNvidiaDriverPath(hw) || LatencyGating.HasAmdDriverPath(hw);
        if (!hasDriverPath)
            return false;

        var display = _display.GetPrimaryGameDisplay();
        if (!display.IsOk || display.Value is not { SupportsVrr: true } ecran)
            return false;
        if (LatencyGating.HasNvidiaDriverPath(hw))
            return true;

        // Sur AMD, il n'est proposé
        // que si FRTC, Chill, la Vsync et Enhanced Sync sont lus, Chill et Enhanced Sync éteints (allumer la Vsync éteint
        // Enhanced Sync, et la rendre ne le rallume pas), et le combo pas déjà en place.
        return _adlx is { IsAvailable: true }
            && _adlx.GetChillActif() is { IsOk: true, Value: false }
            && _adlx.GetEnhancedSyncActif() is { IsOk: true, Value: false }
            && _adlx.GetFrameRateTarget() is { IsOk: true } frtc
            && _adlx.GetVsyncMode() is { IsOk: true } vsync
            && (!PlafondVrr.EstBasseLatence(frtc.Value, ecran.RefreshHz) || vsync.Value != VsyncAmdToujours);
    }

    /// <summary>ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE.WFVR_ALWAYS_ON (ADLXDefines.h).</summary>
    private const int VsyncAmdToujours = 3;

    /// <summary>Carte AMD sans NVAPI, mais FRTC ou Chill illisible : rien n'est proposé, et c'est dit.</summary>
    public override bool EtatNonLu(SettingsState state)
        => !_nvApi.IsAvailable && _adlx is { IsAvailable: true }
           && (!_adlx.GetFrameRateTarget().IsOk || !_adlx.GetChillActif().IsOk
               || !_adlx.GetVsyncMode().IsOk || !_adlx.GetEnhancedSyncActif().IsOk);

    private static int Cible(int rafraichissementHz) => Math.Min(PlafondVrr.Cible(rafraichissementHz), FrlMaxFps);

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
        // Les valeurs posées (Vsync, FRTC), pour que la désinstallation ne rende une feuille que si elle porte
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

            // Pas de plafond à l'aveugle : un écran sans VRR est hors du champ de ce levier.
            if (!display.Value.SupportsVrr)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                    "No VRR/G-Sync/FreeSync display detected: no blind frame cap.");

            if (!_nvApi.IsAvailable)
                return _adlx is { IsAvailable: true }
                    ? await ApplyAmdAsync(Cible(display.Value.RefreshHz)).ConfigureAwait(false)
                    : Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                        "Neither NVAPI nor ADLX is available to configure the frame cap.");

            var targetCap = (uint)Cible(display.Value.RefreshHz);

            // Relever les vraies origines avant d'écrire (retour composite fidèle), au scope écrit.
            var jeu = _game?.RunningGameExecutablePath();
            var vsync = PlanifierDrs(_nvApi, _game is not null, jeu, VsyncModeSettingId, VsyncOn, _nvApi.GetVsyncMode, "Vsync mode");
            if (!vsync.IsOk)
                return Result<TweakSnapshot>.Fail(vsync.Error!.Value.Code, vsync.Error.Value.Message);
            var cap = PlanifierDrs(_nvApi, _game is not null, jeu, FrameRateLimiterSettingId, targetCap, _nvApi.GetFrameRateLimit,
                "frame-rate limit");
            if (!cap.IsOk)
                return Result<TweakSnapshot>.Fail(cap.Error!.Value.Code, cap.Error.Value.Message);

            // Un profil de jeu lisible pour un reglage et pas pour l'autre poserait un
            // combo moitie au jeu, moitie au pilote entier. Les deux au meme endroit, ou rien.
            if (vsync.Value.Op.ProfileName != cap.Value.Op.ProfileName)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Vsync and the frame cap would not land in the same driver profile.");

            // Ops inverses (rendent les deux origines). Ordre : plafond puis vsync (l'inverse de la pose).
            var inverseOps = new PrivilegedOp[] { cap.Value.Inverse, vsync.Value.Inverse };
            var snapshotJson = CompositeSnapshotCodec.Encode(inverseOps);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            // Pose Vsync ON, puis le plafond d'images. Chaque op est appariée à l'inverse qui rend
            // l'état d'entrée du levier. Si la 2e op échoue, on rejoue l'inverse de la 1re (déjà
            // posée) (LIFO) pour que rien ne reste à moitié posé, puis on rend Fail.
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

            // Les valeurs posees (Vsync, plafond), pour que la desinstallation ne rende un
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

    /// <summary>Retour composite : rejoue chaque op d'origine relevée. Snapshot malformé
    /// → SAFETY.SNAPSHOT_INVALID ; ne lève jamais, ne lance jamais une op inconnue.</summary>
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        // Sur AMD, l'inverse est une seule op (FRTC), pas un composite. Lue comme composite, elle serait
        // refusée à chaque remise et le plafond resterait posé sur la carte. Une op seule passe par le chemin de la base.
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
                    return res; // rend tel quel le premier échec de retour
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

}
