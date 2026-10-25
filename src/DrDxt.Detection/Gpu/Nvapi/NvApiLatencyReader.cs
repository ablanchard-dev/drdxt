using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.Detection.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiLatencyReader : la vraie couture de lecture INvApi, sur la frontière NvApiNative vérifiée.
//
// À la place du StubNvApi qui échoue proprement, pour que les leviers de latence (VrrVsyncCap,
// RenderQueueCap, PreferMaxPerf) relèvent leur vraie valeur DRS d'origine et la rendent
// fidèlement. Chaque lecture DRS passe par NvApiNative.TryGetDrsGlobalSetting : une seule session
// DRS NVAPI non élevée (CreateSession -> LoadSettings -> GetBaseProfile -> GetSetting -> DestroySession),
// validée en vrai sur une RTX 4060. Les ids sont vérifiés mot pour mot contre NvApiDriverSettings.h
// et imposés par OpAllowlist. Low Latency Mode n'est pas un réglage DRS : c'est le DWORD de
// registre NVTweak que pose NvidiaUltraLowLatencyTweak, dont rien ne prouve que le pilote le lise ; il se lit là, et
// aucun écran du produit ne s'en sert.
//
// Échec propre : pas de GPU ou de pilote NVIDIA -> IsAvailable faux et chaque lecture Fail(TWEAK.SDK_UNAVAILABLE).
// Un réglage DRS absent du profil (NVAPI_SETTING_NOT_FOUND) échoue aussi, jamais un 0 inventé.
// -----------------------------------------------------------------------
public sealed class NvApiLatencyReader : INvApi
{
    // Ids des réglages DRS, vérifiés contre NvApiDriverSettings.h (github.com/NVIDIA/nvapi).
    private const uint VsyncModeId = 0x00A879CF;        // VSYNCMODE_ID
    private const uint FrameRateLimitId = 0x10835002;   // FRL_FPS_ID
    private const uint PreRenderLimitId = 0x007BA09E;   // PRERENDERLIMIT_ID
    private const uint PreferredPstateId = 0x1057EB71;  // PREFERRED_PSTATE_ID
    private const uint VrrModeId = 0x1194F158;          // VRR_MODE_ID (« Configurer G-SYNC »)

    // Low Latency Mode vit dans le registre NVTweak, pas dans DRS (comme NvidiaUltraLowLatencyTweak).
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string LowLatencyName = "LowLatencyMode";

    private readonly INvApiNative _native;
    private readonly IRegistryReader _registry;

    public NvApiLatencyReader(INvApiNative native, IRegistryReader registry)
        => (_native, _registry) = (native, registry);

    public bool IsAvailable => _native.IsAvailable;

    public Result<uint> GetVsyncMode() => ReadDrs(VsyncModeId);
    public Result<uint> GetFrameRateLimit() => ReadDrs(FrameRateLimitId);
    public Result<uint> GetMaxPreRenderedFrames() => ReadDrs(PreRenderLimitId);
    public Result<uint> GetPreferredPstate() => ReadDrs(PreferredPstateId);

    // Absent du profil de base (NVAPI_SETTING_NOT_FOUND) : au défaut du pilote. La lecture brute, elle, reste un échec : un
    // inverse ne se construit pas sur un défaut que seule une suppression rendrait.
    public bool PreferredPstateAuDefaut() => _native.DrsGlobalAuDefaut(PreferredPstateId);
    public Result<uint> GetGsyncMode() => ReadDrs(VrrModeId);

    public Result<uint> GetLowLatencyMode()
    {
        var r = _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName);
        if (!r.IsOk)
            return Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "Low Latency Mode not readable.");
        return Result<uint>.Ok(r.Value ?? 0); // valeur absente = défaut du pilote (Off = 0).
    }

    /// <summary>Le plafond d'images et l'origine de sa valeur. <see cref="GetFrameRateLimit"/>
    /// répond « combien » ; ceci répond « qui l'a posé », la question qui sépare une valeur livrée
    /// par NVIDIA d'une valeur choisie par quelqu'un.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetFrameRateLimitWithOrigin()
    {
        if (!_native.TryReadDrsGlobalSetting(FrameRateLimitId, out var r))
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "NVIDIA driver setting not readable (NVAPI unavailable or setting at default).");

        return Result<(uint, DrsOriginState)>.Ok((r.Value,
            DrsSettingOrigin.Describe(r.SettingLocation, r.IsCurrentPredefined, r.IsPredefinedValid)));
    }

    /// <summary>Lecture d'origine pour la couture : la même lecture DRS, qui ne garde que la provenance.</summary>
    public Result<DrsOriginState> GetFrameRateLimitOrigin()
    {
        var r = GetFrameRateLimitWithOrigin();
        return r.IsOk
            ? Result<DrsOriginState>.Ok(r.Value.Origin)
            : Result<DrsOriginState>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
    }

    /// <summary>Le plafond d'images que le pilote applique à un seul jeu, avec sa provenance.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetFrameRateLimitForApplication(string applicationPath)
        => GetDrsForApplication(FrameRateLimitId, applicationPath);

    /// <summary>Un réglage DRS tel que le pilote l'applique à un seul jeu, avec sa provenance.
    /// Échoue quand l'exécutable n'a aucun profil (le pilote ne se replie pas alors sur le profil de
    /// base). Quand le profil existe sans posséder le réglage, le pilote rend la valeur héritée
    /// avec settingLocation = GLOBAL_PROFILE (mesuré sur cod.exe le 10/09/2026) : l'origine le dit,
    /// et l'appelant doit la lire avec la valeur.</summary>
    public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
    {
        // Un nom de fichier nu fait répondre au pilote EXECUTABLE_PATH_IS_AMBIGUOUS ; on le refuse ici
        // pour que l'échec nomme la vraie cause au lieu de sortir en erreur du pilote.
        if (string.IsNullOrWhiteSpace(applicationPath) || !Path.IsPathFullyQualified(applicationPath))
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "A fully qualified executable path is required to find its driver profile.");

        var lecture = _native.LireDrsAuJeu(applicationPath, settingId, out var r);
        if (lecture == INvApiNative.LectureAuJeu.PasDeProfil)
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile,
                "This executable has no driver profile of its own: the driver applies the global one.");
        if (lecture != INvApiNative.LectureAuJeu.Lu)
            return Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
                "The game's driver profile could not be read.");

        return Result<(uint, DrsOriginState)>.Ok((r.Value,
            DrsSettingOrigin.Describe(r.SettingLocation, r.IsCurrentPredefined, r.IsPredefinedValid)));
    }

    private Result<uint> ReadDrs(uint settingId) =>
        _native.TryGetDrsGlobalSetting(settingId, out var value)
            ? Result<uint>.Ok(value)
            : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable,
                "NVIDIA driver setting not readable (NVAPI unavailable or setting at default).");
}
