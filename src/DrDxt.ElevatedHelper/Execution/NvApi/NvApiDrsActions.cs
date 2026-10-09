using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiDrsActions — the real DRS (driver settings) write (m2smoke boundary).
//
// The SAFE, public NVAPI surface: a single global driver setting (no voltage / no
// clock). The session sequence (recipe §C: CreateSession → LoadSettings →
// GetBaseProfile → SetSetting → SaveSettings → DestroySession, DestroySession always
// runs) lives in the SINGLE verified native boundary — NvApiNative in DrDxt.GpuNative,
// which shares the NVAPI entry with the clock/power writes. There is NO second copy of
// the DRS function IDs or the NVDRS_SETTING_V1 struct here; this is a thin orchestrator,
// exactly like ADLXTuningActions over IAdlxNative.
//
// The settingId handed here is already allowlist-validated (the closed corrected id
// set: PRERENDERLIMIT / VSYNCMODE / FRL_FPS / PREFERRED_PSTATE — four since phase 18). On an AMD / no-NVIDIA box NVAPI is
// unavailable → a clean Result.Fail("NVIDIA NVAPI unavailable"). Never throws.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvApiDrsActions : INvApiDrsActions
{
    private const string Unavailable = "NVIDIA NVAPI unavailable";

    private readonly INvApiNative _native;

    public NvApiDrsActions() : this(new NvApiNative()) { }

    internal NvApiDrsActions(INvApiNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public Result SetGlobalSetting(uint settingId, uint value)
    {
        if (!_native.IsAvailable)
            return Fail(Unavailable);

        try
        {
            return _native.TrySetDrsGlobalSetting(settingId, value)
                ? Result.Ok()
                : Fail("DRS write failed (session or setting fault).");
        }
        catch (Exception ex)
        {
            // The native boundary already swallows faults (returns false); last-resort guard
            // so an exception never crosses the IPC boundary.
            return Fail($"DRS write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// L2 — l'ecriture par JEU, et sa verification.
    ///
    /// La relecture n'est pas une precaution de style : c'est la meme regle que les ecritures de
    /// tuning (« une action relit avant d'affirmer »). Un `true` natif dit que le driver a
    /// ACCEPTE L'APPEL, pas que la valeur a atterri. Ici deux choses peuvent la faire diverger :
    /// NVIDIA documente qu'une sauvegarde de session ecrase ce qu'un autre processus a modifie
    /// entre-temps (PG-12072-001 p.11), et un profil predefini peut se comporter autrement que
    /// prevu.
    ///
    /// La relecture passe par une SESSION neuve. Relire dans la session d'ecriture rendrait ce
    /// qu'on vient d'y poser -- une verification qui se verifie elle-meme, verte quoi qu'il
    /// arrive. C'est le cas ou un test peut NOMMER une garantie sans la prouver.
    ///
    /// Une relecture qui echoue n'est pas un succes : « applique mais non confirme » est
    /// exactement l'affirmation que ce code existe pour empecher.
    /// </summary>
    public Result SetSettingForApplication(string applicationPath, uint settingId, uint value)
    {
        if (!_native.IsAvailable)
            return Fail(Unavailable);

        if (string.IsNullOrWhiteSpace(applicationPath))
            return Fail("A per-application DRS write needs the game's executable path.");

        try
        {
            if (!_native.TrySetDrsSettingForApplication(applicationPath, settingId, value))
                return Fail("DRS per-application write failed "
                          + "(the executable has no profile of its own, the path is ambiguous, or a driver fault). "
                          + "No profile was created and the base profile was NOT written instead.");

            // Session neuve : on redemande au driver ce qu'il applique vraiment a cet executable.
            if (!_native.TryReadDrsSettingForApplication(applicationPath, settingId, out var readBack))
                return Fail("The DRS write was accepted but could not be read back, "
                          + "so it is NOT confirmed.");

            // Comparaison EXACTE, comme pour les ecritures de tuning : un driver qui quantifierait
            // une demande apparaitrait ici comme un ecart -- une information sur ce driver, pas un
            // defaut a masquer avec une tolerance inventee.
            return readBack.Value == value
                ? Result.Ok()
                : Fail($"The DRS setting was not applied: asked {value}, the driver holds {readBack.Value} "
                     + "for this executable (it accepted the call and kept another value).");
        }
        catch (Exception ex)
        {
            return Fail($"DRS per-application write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// PHASE 18 F1 lot 3 — le revert d'une ecriture au profil d'un jeu qui ne possedait pas le
    /// reglage.
    ///
    /// La preuve est la PROVENANCE relue, en session neuve. Relire la valeur seule ne prouve
    /// rien : une reecriture de la meme valeur la rendrait aussi, en laissant au jeu un reglage
    /// « pose par toi » qu'il n'avait pas.
    ///
    /// Idempotent sans deviner les codes d'erreur NVAPI : si l'appel est refuse (par exemple parce
    /// que le profil ne possede deja plus le reglage) mais que la relecture montre la provenance
    /// attendue, l'etat voulu est la, et c'est un succes. L'inverse ne l'est jamais.
    /// </summary>
    public Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue)
    {
        if (!_native.IsAvailable)
            return Fail(Unavailable);

        if (string.IsNullOrWhiteSpace(applicationPath))
            return Fail("A per-application DRS delete needs the game's executable path.");

        try
        {
            var accepte = _native.TryDeleteDrsSettingForApplication(applicationPath, settingId);

            if (!_native.TryReadDrsSettingForApplication(applicationPath, settingId, out var relu))
                return Fail("The DRS delete could not be read back, so the return to the original "
                          + "state is NOT confirmed.");

            var origine = DrsSettingOrigin.Describe(
                relu.SettingLocation, relu.IsCurrentPredefined, relu.IsPredefinedValid).Origin;

            return origine == origineAttendue
                ? Result.Ok()
                : Fail($"The DRS setting did not return to its original state: expected {origineAttendue}, "
                     + $"the driver reports {origine} for this executable "
                     + $"(delete call {(accepte ? "accepted" : "refused")}).");
        }
        catch (Exception ex)
        {
            return Fail($"DRS per-application delete failed: {ex.GetType().Name}.");
        }
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
