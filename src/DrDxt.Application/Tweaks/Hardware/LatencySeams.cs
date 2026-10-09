using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Injectable hardware seams for the M2 Phase D latency tweaks.
//
// These are read + CAPABILITY seams only — they never perform the privileged
// write themselves. The actual change is always applied through IElevatedClient
// with a typed, allowlisted PrivilegedOp (Phase A). The seams exist so each
// tweak can:
//   * decide gating (capability / device class), and
//   * capture the real original value before applying (for a faithful revert).
//
// Every method returns a Result so an unavailable SDK / registry path fails
// CLEAN (TWEAK.SDK_UNAVAILABLE) — it must never throw. Implementations live in
// Detection / Infrastructure (P/Invoke); unit tests use NSubstitute fakes.
// -----------------------------------------------------------------------

/// <summary>Read seam over the NVIDIA driver (NVAPI / NVCP registry) for the
/// latency tweaks. Apply is done via IElevatedClient; this only reads.</summary>
public interface INvApi
{
    /// <summary>True when an NVIDIA driver / NVAPI is present and usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the current driver "Low Latency Mode" (NVTweak) value.
    /// 0 = Off, 1 = On, 2 = Ultra. Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetLowLatencyMode();

    /// <summary>Reads the current DRS "Max Pre-Rendered Frames" value for the render
    /// queue cap. Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetMaxPreRenderedFrames();

    /// <summary>Reads the current driver Vsync DRS value. Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetVsyncMode();

    /// <summary>Reads the current driver frame-rate-limit (fps). 0 = no cap.
    /// Unavailable path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetFrameRateLimit();

    /// <summary>Who put the frame cap there, as the driver reports it on the same read.
    /// Unavailable path -> Fail(TWEAK.SDK_UNAVAILABLE): "not read", never "nobody set it".</summary>
    Result<DrsOriginState> GetFrameRateLimitOrigin();

    /// <summary>Reads the current DRS "Power management mode" (PREFERRED_PSTATE) value, so
    /// the Prefer-Max-Perf tweak can capture the real original for a faithful revert.
    /// 0 = Adaptive, 1 = Prefer max performance (NvApiDriverSettings.h). Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetPreferredPstate();

    /// <summary>PHASE 18 F1 — un reglage DRS tel que le pilote l'applique a un executable, avec sa
    /// provenance. Le profil de base n'est pas ce que le jeu recoit : un profil de jeu peut porter
    /// sa propre valeur, et ecrire ou relire le global validerait alors un reglage sans effet.
    /// Par defaut : Fail. Une couture qui ne sait pas lire au scope du jeu le dit ; elle ne rend
    /// jamais la valeur globale a la place.</summary>
    Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
        => Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
            "This seam cannot read a driver setting at game scope.");

    /// <summary>25/09/2026 — le reglage « Configurer G-SYNC » du pilote (DRS VRR_MODE, NVIDIA
    /// NvApiDriverSettings.h) : 0 desactive, 1 plein ecran seulement, 2 plein ecran et fenetre.
    /// Par defaut : Fail, « non lu ».</summary>
    Result<uint> GetGsyncMode()
        => Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the G-SYNC mode.");
}

/// <summary>PHASE 18 F1 — le chemin complet de l'executable du jeu en cours, ou null. C'est la cle
/// que le pilote NVIDIA utilise pour choisir le profil qu'il applique ; sans elle, toute lecture
/// DRS decrit le profil global, pas le jeu.</summary>
public interface IGameExecutable
{
    string? RunningGameExecutablePath();
}

/// <summary>Read seam over the AMD ADLX runtime for the latency tweaks (Anti-Lag
/// is the AMD render-queue / low-latency floor). Apply is done via IElevatedClient.</summary>
public interface IAdlx
{
    /// <summary>True when the AMD ADLX runtime is present and usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the current Anti-Lag state (0 = off, 1 = on). Unavailable
    /// path → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetAntiLagState();

    /// <summary>A1 (06/10/2026) — la limite d'images que le pilote AMD impose (FRTC ou Chill), en images/s ;
    /// 0 = aucune. Par défaut : Fail, « non lu ».</summary>
    Result<int> GetFrameRateLimit()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the AMD frame limit.");

    /// <summary>A2b (07/10/2026) — FRTC en un entier (<c>AdlxTuningKind.FrameRateTarget</c>) : allumé = sa cible,
    /// éteint = moins la cible réglée. Par défaut : Fail, « non lu ».</summary>
    Result<int> GetFrameRateTarget()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read AMD FRTC.");

    /// <summary>A2c (08/10/2026) — le mode Vsync du pilote AMD (0 à 3, <c>AdlxTuningKind.VerticalRefresh</c>).</summary>
    Result<int> GetVsyncMode()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the AMD Vsync mode.");

    /// <summary>A2c — Enhanced Sync est-il allumé ? La Vsync du pilote l'éteint en s'allumant (I3DSettings.h).</summary>
    Result<bool> GetEnhancedSyncActif()
        => Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read AMD Enhanced Sync.");

    /// <summary>A2 (07/10/2026) — Radeon Chill est-il allumé ? Anti-Lag l'éteint en s'allumant (I3DSettings.h :
    /// fonctions exclusives), et l'éteindre ensuite ne le rallume pas. Par défaut : Fail, « non lu ».</summary>
    Result<bool> GetChillActif()
        => Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read Radeon Chill.");
}

/// <summary>Information about a single display, used for VRR gating (D3) and for the
/// "panel running below its own capability" finding.</summary>
public sealed record DisplayInfo(
    bool IsPrimaryGameDisplay,
    bool SupportsVrr /* G-Sync / FreeSync capable AND enabled */,
    int RefreshHz,
    // Trailing/defaulted so existing constructions stay source-compatible.
    /// <summary>Highest refresh the panel offers at the current resolution. 0 = not read.
    /// Never defaulted to <see cref="RefreshHz"/>: "we did not read the maximum" and "the
    /// panel is already at its maximum" are different answers, and collapsing them would
    /// hide the deficit this field exists to expose.</summary>
    int MaxRefreshHz = 0,
    string? DeviceName = null,
    // L8 suite — le VRR lu au pilote (NVAPI). Unknown = non lu, jamais Off. `SupportsVrr` n'est
    // vrai que si les deux sont On.
    TriState VrrPossible = TriState.Unknown,
    TriState VrrEnabled = TriState.Unknown,
    // 06/10/2026 (chantier B2) : la résolution courante et le nom d'interface du moniteur, pour relire son EDID.
    int Largeur = 0,
    int Hauteur = 0,
    string? NomInterface = null,
    // 06/10/2026 (chantier B3) : la carte qui pilote cet écran (EnumDisplayDevices), pour voir un écran branché sur la
    // carte mère.
    string? Adaptateur = null)
{
    /// <summary>
    /// The panel can do better than what Windows is driving it at. Proven arithmetic, not an
    /// estimate: the maximum the panel offers minus the frequency it is running.
    /// <para>
    /// Found on the author's own machine: a panel capable of 200 Hz driven at 144. The NVIDIA
    /// low-latency rule caps the frame rate 3 under the refresh, so it produced 141 — and that
    /// 141 was the only visible trace of the real defect. Fixing the cap would have fixed
    /// nothing; the panel was the deficit and the cap was its faithful symptom.
    /// </para>
    /// Unknown maximum deducts nothing, in line with the rest of the scoring doctrine.
    /// </summary>
    public bool IsBelowCapability => MaxRefreshHz > 0 && RefreshHz > 0 && MaxRefreshHz > RefreshHz;

    /// <summary>Frames per second the panel can present and is not being asked to.</summary>
    public int RefreshHeadroomHz => IsBelowCapability ? MaxRefreshHz - RefreshHz : 0;
}

/// <summary>Read seam over the display configuration. Used by the VRR/Vsync cap
/// tweak to detect whether a G-Sync/FreeSync display is present before configuring
/// a low-latency cap (no blind cap on a non-VRR panel).</summary>
public interface IDisplayInfo
{
    /// <summary>Returns the primary game display (the one a fullscreen game targets).
    /// Unavailable / ambiguous → Fail(TWEAK.SDK_UNAVAILABLE) so the tweak skips
    /// cleanly rather than capping the wrong screen.</summary>
    Result<DisplayInfo> GetPrimaryGameDisplay();

    /// <summary>Every display attached to the desktop, primary first.
    /// <para>
    /// A single display is not enough to answer the question that matters on a multi-screen
    /// desk: a second, faster panel is invisible to <see cref="GetPrimaryGameDisplay"/>, so
    /// nothing can report that the game is running on the slower one.
    /// </para></summary>
    Result<IReadOnlyList<DisplayInfo>> GetAllDisplays();
}

/// <summary>Minimal registry-read seam (NO write — writes go through the elevated
/// helper). Lets a tweak capture the real original DWORD value before applying so
/// the snapshot carries a faithful inverse op. A missing key → Ok(null): the value
/// is simply not present yet (its "original" is absence). A broken registry path →
/// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
public interface IRegistryReader
{
    /// <summary>Reads a DWORD registry value. Returns Ok(null) when the value does
    /// not exist (capture = "absent"); Fail(TWEAK.SDK_UNAVAILABLE) on a read error.</summary>
    Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name);

    /// <summary>Reads a REG_SZ string value. Returns Ok(null) when the value does not exist
    /// (capture = "absent"); Fail(TWEAK.SDK_UNAVAILABLE) on a read error. Used by tweaks that
    /// must read-modify-write a composite string (e.g. DirectXUserGlobalSettings).</summary>
    Result<string?> ReadString(RegistryHiveKind hive, string path, string name);

    /// <summary>Lit un REG_MULTI_SZ et rend ses entrees JOINTES par des sauts de ligne —
    /// la meme forme que celle transportee par SetRegistryValueOp, pour que capture et
    /// rejeu soient symetriques. Ok(null) = valeur absente (capture = « absente ») ;
    /// Fail(TWEAK.SDK_UNAVAILABLE) en cas d'erreur de lecture. Jamais une valeur inventee.</summary>
    Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name);
}
