using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// VrrVsyncCapConfigTweak : le combo basse latence de Blur Busters pour un écran VRR (G-Sync, FreeSync), dans le pilote :
// Vsync activée et plafond ~3 images/s sous la fréquence (fréquence − 3, affichée), dans la fenêtre VRR, sans
// déchirure. Seulement sur un écran VRR détecté (IDisplayInfo), l'écran de jeu principal ; ambigu, rien. Au profil du
// jeu, comme PreferMaxPerf (« Base Profile » toucherait tous les jeux et le bureau). Origines dans un snapshot
// composite ; jamais d'exception.
// -----------------------------------------------------------------------
/// <summary>Pose Vsync et plafond d'images (fréquence − 3) dans le pilote, au profil du jeu. Proposé quand l'écran de jeu
/// est lu VRR, sur NVIDIA, ou sur AMD si Chill et Enhanced Sync sont lus éteints et le combo pas déjà en place.</summary>
public sealed class VrrVsyncCapConfigTweak : LatencyTweakBase
{
    // Ids DRS vérifiés contre le NvApiDriverSettings.h officiel (source : github.com/NVIDIA/nvapi) : VSYNCMODE_ID =
    // 0x00A879CF, FRL_FPS_ID = 0x10835002. Les valeurs 0x00fc00fa (VSYNC) et 0x10835000 (FRL) sont fausses.
    private const uint VsyncModeSettingId = 0x00A879CF;        // VSYNCMODE_ID
    private const uint FrameRateLimiterSettingId = 0x10835002;  // FRL_FPS_ID
    // VSYNCMODE : des constantes, pas un 0/1 (NvApiDriverSettings.h : PASSIVE=0x60925292, FORCEON=0x47814940,
    // FORCEOFF=0x08416747 ; une RTX 4060 réelle lit PASSIVE). « Vsync ON » = FORCEON ; 1 serait une valeur invalide.
    private const uint VsyncOn = 0x47814940;                   // VSYNCMODE_FORCEON
    // La cible : PlafondVrr (Core), la plus basse de « fréquence − 3 » et de la règle de Reflex.
    // Plage documentée de FRL_FPS = 0-1023 fps (NvApiDriverSettings.h) : jamais d'écriture hors plage.
    private const int FrlMaxFps = 1023;

    private readonly INvApi _nvApi;
    private readonly IDisplayInfo _display;
    private readonly IGameExecutable? _game;
    private readonly IAdlx? _adlx;

    /// <param name="game">Le jeu en cours. Absent (outils, tests) : ecriture globale. Un jeu connu sans profil lisible
    /// est refuse (LatencyTweakBase.PlanifierDrs) : son reglage ne part jamais dans le profil global.</param>
    /// <param name="adlx">Le pilote AMD. Sans NVAPI, le plafond passe par FRTC (ADLX), et la Vsync du pilote AMD est mise
    /// « toujours activée », l'équivalent de FORCEON chez NVIDIA.</param>
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
        if (!display.IsOk || display.Value is not { SupportsVrr: true } ecran || !FrequenceLue(ecran))
            return false;
        // Sans jeu ouvert, la pose NVIDIA (au profil du jeu) échoue toujours et rien ne se lit : non lu, dit par EtatNonLu.
        if (LatencyGating.HasNvidiaDriverPath(hw))
            return !SansJeuPourNvidia() && !ComboNvidiaDejaPose(ecran);

        // Sur AMD : FRTC, Chill, Vsync et Enhanced Sync lus, Chill et Enhanced Sync éteints (allumer la Vsync éteint
        // Enhanced Sync, la rendre ne le rallume pas), et le combo pas déjà en place.
        return _adlx is { IsAvailable: true }
            && _adlx.GetChillActif() is { IsOk: true, Value: false }
            && _adlx.GetEnhancedSyncActif() is { IsOk: true, Value: false }
            && _adlx.GetFrameRateTarget() is { IsOk: true } frtc
            && _adlx.GetVsyncMode() is { IsOk: true } vsync
            && (!PlafondVrr.EstBasseLatence(frtc.Value, FrequenceDuPlafond(ecran)) || vsync.Value != VsyncAmdToujours);
    }

    /// <summary>Vsync et plafond déjà à la cible, lus là où la pose écrirait (le profil du jeu ouvert, sinon le global quand
    /// rien ne cherche de jeu) : le levier n'a rien à poser. Une lecture ratée ou un seul des deux en place : à poser.</summary>
    private bool ComboNvidiaDejaPose(DisplayInfo ecran)
    {
        if (!_nvApi.IsAvailable) return false;
        var cible = (uint)Cible(FrequenceDuPlafond(ecran));
        var jeu = _game?.RunningGameExecutablePath();
        if (jeu is not null)
            return _nvApi.GetDrsForApplication(VsyncModeSettingId, jeu) is { IsOk: true } v && v.Value.Value == VsyncOn
                && _nvApi.GetDrsForApplication(FrameRateLimiterSettingId, jeu) is { IsOk: true } c && c.Value.Value == cible;
        return _game is null
            && _nvApi.GetVsyncMode() is { IsOk: true, Value: VsyncOn }
            && _nvApi.GetFrameRateLimit() is { IsOk: true } plafond && plafond.Value == cible;
    }

    /// <summary>ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE.WFVR_ALWAYS_ON (ADLXDefines.h).</summary>
    private const int VsyncAmdToujours = 3;

    /// <summary>La pose NVIDIA cherche un jeu et n'en trouve aucun ouvert.</summary>
    private bool SansJeuPourNvidia() => _nvApi.IsAvailable && _game is not null && _game.RunningGameExecutablePath() is null;

    /// <summary>Carte AMD sans NVAPI, mais FRTC ou Chill illisible ; écran VRR dont la fréquence n'est pas lue ; ou carte NVIDIA
    /// sans jeu ouvert, dont le profil ne se lit pas : rien n'est proposé, et c'est dit.</summary>
    public override bool EtatNonLu(SettingsState state)
        => (_display.GetPrimaryGameDisplay() is { IsOk: true, Value: { SupportsVrr: true } ecran } && (!FrequenceLue(ecran) || SansJeuPourNvidia()))
           || (!_nvApi.IsAvailable && _adlx is { IsAvailable: true }
               && (!_adlx.GetFrameRateTarget().IsOk || !_adlx.GetChillActif().IsOk
                   || !_adlx.GetVsyncMode().IsOk || !_adlx.GetEnhancedSyncActif().IsOk));

    // Win32 rend 0 ou 1 pour « fréquence par défaut du matériel » (dmDisplayFrequency) : la cible calculée dessus vaudrait
    // 1 image/s.
    private static bool FrequenceLue(DisplayInfo ecran) => ecran.RefreshHz > 3;

    private static int Cible(int rafraichissementHz) => Math.Min(PlafondVrr.Cible(rafraichissementHz), FrlMaxFps);

    /// <summary>La fréquence sur laquelle se calcule le plafond : la maximale de l'écran quand elle est lue. Un écran 144 Hz
    /// laissé à 60 recevrait sinon un plafond à 57, qui briderait le jeu dès que l'écran repasse à 144.</summary>
    private static int FrequenceDuPlafond(DisplayInfo ecran) => Math.Max(ecran.RefreshHz, ecran.MaxRefreshHz);

    /// <summary>Sur AMD : Vsync du pilote « toujours activée » puis FRTC à la cible, chacun seulement s'il n'y est pas
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
        // Les valeurs posées : la désinstallation ne rend une feuille que si elle porte encore celle de DrDXT.
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
            if (!FrequenceLue(display.Value))
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "The display refresh rate was not read: no frame cap computed from it.");

            if (!_nvApi.IsAvailable)
                return _adlx is { IsAvailable: true }
                    ? await ApplyAmdAsync(Cible(FrequenceDuPlafond(display.Value))).ConfigureAwait(false)
                    : Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                        "Neither NVAPI nor ADLX is available to configure the frame cap.");

            var targetCap = (uint)Cible(FrequenceDuPlafond(display.Value));

            // Relever les vraies origines avant d'écrire (retour composite fidèle), au scope écrit.
            var jeu = _game?.RunningGameExecutablePath();
            var vsync = PlanifierDrs(_nvApi, _game is not null, jeu, VsyncModeSettingId, VsyncOn, _nvApi.GetVsyncMode, "Vsync mode");
            if (!vsync.IsOk)
                return Result<TweakSnapshot>.Fail(vsync.Error!.Value.Code, vsync.Error.Value.Message);
            var cap = PlanifierDrs(_nvApi, _game is not null, jeu, FrameRateLimiterSettingId, targetCap, _nvApi.GetFrameRateLimit,
                "frame-rate limit");
            if (!cap.IsOk)
                return Result<TweakSnapshot>.Fail(cap.Error!.Value.Code, cap.Error.Value.Message);

            // Les deux reglages au meme profil, ou rien (sinon moitie au jeu, moitie au pilote entier).
            if (vsync.Value.Op.ProfileName != cap.Value.Op.ProfileName)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Vsync and the frame cap would not land in the same driver profile.");

            // Ops inverses (rendent les deux origines). Ordre : plafond puis vsync (l'inverse de la pose).
            var inverseOps = new PrivilegedOp[] { cap.Value.Inverse, vsync.Value.Inverse };
            var snapshotJson = CompositeSnapshotCodec.Encode(inverseOps);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            // Vsync ON puis plafond, chaque op appariée à son inverse : la 2e échoue, l'inverse de la 1re est rejoué.
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

            // Les valeurs posees : la desinstallation ne rend un reglage que s'il porte encore celle de DrDXT.
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
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        // ApplyAmdAsync écrit toujours un composite ; un ancien instantané AMD portait une seule op (FRTC). Lue comme
        // composite, elle serait refusée à chaque remise et le plafond resterait posé : une op seule passe par la base.
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
            return await RetourArriere.ToutRendreAsync(Elevated, decoded.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

}
