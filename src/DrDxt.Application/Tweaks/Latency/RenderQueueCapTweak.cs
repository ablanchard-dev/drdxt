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
// les jeux et le bureau. Elle ne vaut qu'en DirectX 9 à 11 (en DX12 et Vulkan, le moteur du jeu gère lui-même la file) :
// proposée et posée seulement sur un jeu dont l'API se lit DX9/DX11 dans ses modules chargés.
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

    /// <param name="game">Le jeu en cours, son profil et son API graphique. Absent (outils, banc) : écriture globale, sans
    /// lecture de l'API.</param>
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

    internal enum Api { NonLue, Dx9Ou11, Autre }

    /// <summary>L'API du jeu d'après ses modules. d3d12.dll ou vulkan-1.dll chargé l'emporte : un jeu DX11 qui les charge
    /// (une surcouche) n'est pas proposé, ce qui est le côté sûr.</summary>
    internal static Api ApiDuJeu(IReadOnlyList<string>? modules)
    {
        if (modules is null) return Api.NonLue;
        bool A(string nom) => modules.Any(m => string.Equals(m, nom, StringComparison.OrdinalIgnoreCase));
        if (A("d3d12.dll") || A("vulkan-1.dll")) return Api.Autre;
        return A("d3d11.dll") || A("d3d9.dll") ? Api.Dx9Ou11 : Api.Autre;
    }

    private Api ApiLue() => ApiDuJeu(_game?.ModulesDuJeu());

    /// <summary>La file du pilote agit sur le jeu en cours : son API est lue DX9/DX11. Sans couture du jeu (outils, banc),
    /// rien ne se cherche et l'écriture va au profil global, comme dans PlanifierDrs.</summary>
    private bool FileDuPiloteAgit() => _game is null || ApiLue() == Api.Dx9Ou11;

    private enum Carte { Aucune, Nvidia, Amd }

    /// <summary>La carte du levier, la même pour le proposer, le dire non lu et le poser : NVIDIA dès que NVAPI répond, AMD
    /// seulement sans elle, comme les autres leviers de la carte. Un Ryzen à puce graphique AMD avec une RTX charge ADLX pour
    /// la puce : la carte du jeu reste la NVIDIA.</summary>
    private Carte CarteDuLevier() => _nvApi.IsAvailable ? Carte.Nvidia : _adlx.IsAvailable ? Carte.Amd : Carte.Aucune;

    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        if (!LatencyGating.FixeOuPortable(hw))
            return false;
        return CarteDuLevier() switch
        {
            // Sur NVIDIA, « Low Latency Mode : On » (une seule image d'avance), au profil du jeu
            // seulement ; sans profil de jeu, rien ne s'écrit (pas de repli sur le global, voir PlanifierDrs).
            Carte.Nvidia => LatencyGating.HasNvidiaDriverPath(hw) && FileDuPiloteAgit()
                && FileNvidiaAuJeu() is { IsOk: true } lu && lu.Value.Value != RenderQueueCapValue,
            // L'écriture existe (IADLX3DAntiLag.SetEnabled, carte dédiée). Proposé seulement quand Anti-Lag
            // est LU éteint et Chill LU éteint : allumer Anti-Lag éteint Chill, et le rendre ne le rallume pas.
            Carte.Amd => LatencyGating.HasAmdDriverPath(hw)
                && _adlx.GetAntiLagState() is { IsOk: true, Value: 0 }
                && _adlx.GetChillActif() is { IsOk: true, Value: false },
            _ => false,
        };
    }

    /// <summary>Carte AMD, mais Anti-Lag ou Chill illisible : rien n'est proposé, et c'est dit. Sur NVIDIA, la file du profil
    /// du jeu illisible, l'API du jeu ouvert illisible, ou scannée sans jeu ouvert.</summary>
    public override bool EtatNonLu(SettingsState state) => CarteDuLevier() switch
    {
        Carte.Amd => !_adlx.GetAntiLagState().IsOk || !_adlx.GetChillActif().IsOk,
        Carte.Nvidia => FileNvidiaAuJeu() is { IsOk: false } echec && echec.Error?.Code != ErrorCodes.DrsNoGameProfile
            || (_game is not null && (_game.RunningGameExecutablePath() is null || ApiLue() == Api.NonLue)),
        _ => false,
    };

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            switch (CarteDuLevier())
            {
                case Carte.Nvidia:
                    return FileDuPiloteAgit()
                        ? await ApplyNvidiaRenderQueueCapAsync().ConfigureAwait(false)
                        : Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                            "The driver render queue only acts in DirectX 9 to 11; the game's API is not read as such.");
                case Carte.Amd: return await ApplyAntiLagAsync().ConfigureAwait(false);
            }

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
