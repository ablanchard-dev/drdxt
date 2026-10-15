using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// RenderQueueCapTweak.
//
// Le plancher de faible latence pour les deux marques, quand le jeu n'a pas Reflex. Limite la
// profondeur de la file de rendu pour que le processeur ne prépare jamais d'images d'avance sur la
// carte (moins de latence entre l'entrée et l'affichage, sans coût en images) :
//   * AMD : Anti-Lag d'ADLX activé (ApplyAdlxTuningOp) ;
//   * NVIDIA : DRS « Max Pre-Rendered Frames » = 1 (ApplyNvApiDrsSettingOp), au profil du jeu en
//     cours, jamais une écriture de registre brute non prise en charge.
//
// La branche NVIDIA écrit au profil du jeu, comme PreferMaxPerf : « Base Profile » toucherait tous
// les jeux et le bureau.
//
// L'original est relu d'abord, l'op inverse sérialisée rangée dans le snapshot ; chaque frontière
// externe est protégée : échec propre (TWEAK.SDK_UNAVAILABLE), jamais d'exception.
// -----------------------------------------------------------------------
/// <summary>Limite la file de rendu à une image : Anti-Lag sur AMD, « Max Pre-Rendered Frames » = 1 au profil du jeu
/// sur NVIDIA. Proposé quand la valeur est lue autrement (sur AMD, Anti-Lag et Chill lus éteints).</summary>
public sealed class RenderQueueCapTweak : LatencyTweakBase
{
    // Id du réglage DRS NVIDIA de la limite de pré-rendu (PRERENDERLIMIT_ID). La valeur
    // 1 = une seule image pré-rendue (la file la plus courte, la latence la plus basse).
    private const uint MaxPreRenderedFramesSettingId = 0x007ba09e;
    private const uint RenderQueueCapValue = 1;

    private const int AntiLagOn = 1;

    private readonly INvApi _nvApi;
    private readonly IAdlx _adlx;
    private readonly IGameExecutable? _game;

    /// <param name="game">Le jeu en cours. Absent (outils, tests) ou jeu sans profil lisible :
    /// ecriture globale, comme avant.</param>
    public RenderQueueCapTweak(INvApi nvApi, IAdlx adlx, IElevatedClient elevated, IGameExecutable? game = null)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _game = game;
    }

    public override string Id => "latency.render_queue_cap";
    public override string SummaryKey => "tweak.latency.render_queue_cap.floor";

    // Les jeux qui ont Reflex. Quand le jeu l'active, Reflex remplace la file du pilote : l'essai ne
    // mesurerait que du bruit. Source : https://www.nvidia.com/en-us/geforce/technologies/reflex/supported-products/
    // Liste courte des jeux compétitifs les plus joués ; un jeu absent est essayé, mesuré, et rendu s'il ne gagne rien.
    private static readonly HashSet<string> JeuxAvecReflex = new(StringComparer.OrdinalIgnoreCase)
    {
        "cod.exe", "VALORANT-Win64-Shipping.exe", "cs2.exe", "FortniteClient-Win64-Shipping.exe", "r5apex.exe",
        "Overwatch.exe", "TslGame.exe", "RainbowSix.exe", "RainbowSix_DX12.exe", "RainbowSix_Vulkan.exe",
        "Marvel-Win64-Shipping.exe",
    };

    /// <summary>La file du pilote NVIDIA au profil du jeu en cours ; null quand il n'y a rien à lire ici (pas de jeu,
    /// jeu à Reflex, pas de NVAPI).</summary>
    private Result<(uint Value, DrsOriginState Origin)>? FileNvidiaAuJeu()
    {
        var jeu = _game?.RunningGameExecutablePath();
        if (jeu is null || !_nvApi.IsAvailable || JeuxAvecReflex.Contains(Path.GetFileName(jeu))) return null;
        return _nvApi.GetDrsForApplication(MaxPreRenderedFramesSettingId, jeu);
    }

    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        if (!LatencyGating.FixeOuPortable(hw))
            return false;
        // Sur NVIDIA, « Low Latency Mode : On » (une seule image d'avance), au profil du jeu
        // seulement ; sans profil de jeu, rien ne s'écrit (pas de repli sur le global, voir PlanifierDrs).
        if (LatencyGating.HasNvidiaDriverPath(hw))
            return FileNvidiaAuJeu() is { IsOk: true } lu && lu.Value.Value != RenderQueueCapValue;
        // L'écriture existe (IADLX3DAntiLag.SetEnabled, carte dédiée). Proposé seulement quand Anti-Lag
        // est LU éteint et Chill LU éteint : allumer Anti-Lag éteint Chill, et le rendre ne le rallume pas.
        return LatencyGating.HasAmdDriverPath(hw)
            && _adlx.GetAntiLagState() is { IsOk: true, Value: 0 }
            && _adlx.GetChillActif() is { IsOk: true, Value: false };
    }

    /// <summary>Carte AMD, mais Anti-Lag ou Chill illisible : rien n'est proposé, et c'est dit.</summary>
    public override bool EtatNonLu(SettingsState state)
        => (_adlx.IsAvailable && (!_adlx.GetAntiLagState().IsOk || !_adlx.GetChillActif().IsOk))
           || FileNvidiaAuJeu() is { IsOk: false } echec && echec.Error?.Code != ErrorCodes.DrsNoGameProfile
           // Sur NVIDIA, la file se lit au profil du jeu ; scanné sans jeu ouvert, elle n'est pas lue.
           || (_nvApi.IsAvailable && !_adlx.IsAvailable && _game is not null && _game.RunningGameExecutablePath() is null);

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // D'abord Anti-Lag d'AMD ; sinon le plafond DRS de file de rendu de NVIDIA.
            if (_adlx.IsAvailable)
                return await ApplyAntiLagAsync().ConfigureAwait(false);
            if (_nvApi.IsAvailable)
                return await ApplyNvidiaRenderQueueCapAsync().ConfigureAwait(false);

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither ADLX nor NVAPI is available for a render-queue cap.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Render-queue cap apply boundary error: {ex.Message}");
        }
    }

    private async Task<Result<TweakSnapshot>> ApplyAntiLagAsync()
    {
        var capture = _adlx.GetAntiLagState();
        if (!capture.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original Anti-Lag state: {capture.Error?.Code}.");

        if (_adlx.GetChillActif() is not { IsOk: true, Value: false })
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Radeon Chill is on or unread: Anti-Lag would switch it off for good.");

        // GpuIndex 0 : pour Anti-Lag, le helper écrit la carte dédiée (celle que le lecteur lit), l'index est ignoré.
        var inverse = PrivilegedOpCodec.Serialize(
            new ApplyAdlxTuningOp(GpuIndex: 0, AdlxTuningKind.AntiLag, capture.Value));
        if (!inverse.IsOk || inverse.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        var pose = new ApplyAdlxTuningOp(GpuIndex: 0, AdlxTuningKind.AntiLag, AntiLagOn);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // La valeur posée, pour que la désinstallation rende Anti-Lag seulement s'il porte encore la sienne.
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, inverse.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }

    private async Task<Result<TweakSnapshot>> ApplyNvidiaRenderQueueCapAsync()
    {
        var plan = PlanifierDrs(_nvApi, _game is not null, _game?.RunningGameExecutablePath(), MaxPreRenderedFramesSettingId,
            RenderQueueCapValue, _nvApi.GetMaxPreRenderedFrames, "Max-Pre-Rendered-Frames");
        if (!plan.IsOk)
            return Result<TweakSnapshot>.Fail(plan.Error!.Value.Code, plan.Error.Value.Message);
        var (inverse, op) = plan.Value;

        var inverseWire = PrivilegedOpCodec.Serialize(inverse);
        if (!inverseWire.IsOk || inverseWire.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverseWire.Value, op));
    }
}
