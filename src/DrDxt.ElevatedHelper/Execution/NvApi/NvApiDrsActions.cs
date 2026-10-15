using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiDrsActions : la vraie écriture DRS (réglages du pilote), frontière du m2smoke.
//
// La surface NVAPI publique et sûre : un seul réglage global du pilote (ni tension ni
// horloge). La séquence de session (recette §C : CreateSession → LoadSettings →
// GetBaseProfile → SetSetting → SaveSettings → DestroySession, DestroySession tourne toujours)
// vit dans l'unique frontière native vérifiée : NvApiNative dans DrDxt.GpuNative, qui partage
// l'entrée NVAPI avec les écritures d'horloge et de puissance. Aucune seconde copie des ids de
// fonction DRS ni de la struct NVDRS_SETTING_V1 ici ; c'est un orchestrateur mince, exactement
// comme ADLXTuningActions sur IAdlxNative.
//
// Le settingId reçu est déjà validé par l'allowlist (l'ensemble fermé d'ids corrigés :
// PRERENDERLIMIT / VSYNCMODE / FRL_FPS / PREFERRED_PSTATE, quatre). Sur une machine AMD / sans
// NVIDIA, NVAPI est indisponible → un Result.Fail propre (« NVIDIA NVAPI unavailable »). Ne lève jamais.
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
            // La frontière native avale déjà les pannes (rend false) ; garde de dernier recours
            // pour qu'aucune exception ne traverse la frontière IPC.
            return Fail($"DRS write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// L'ecriture par jeu, et sa verification.
    ///
    /// La relecture n'est pas une precaution de style : c'est la meme regle que les ecritures de
    /// tuning (« une action relit avant d'affirmer »). Un `true` natif dit que le driver a
    /// accepte l'appel, pas que la valeur a atterri. Ici deux choses peuvent la faire diverger :
    /// NVIDIA documente qu'une sauvegarde de session ecrase ce qu'un autre processus a modifie
    /// entre-temps (PG-12072-001 p.11), et un profil predefini peut se comporter autrement que
    /// prevu.
    ///
    /// La relecture passe par une session neuve. Relire dans la session d'ecriture rendrait ce
    /// qu'on vient d'y poser : une verification qui se verifie elle-meme, verte quoi qu'il
    /// arrive. C'est le cas ou un test peut nommer une garantie sans la prouver.
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

            // Comparaison exacte, comme pour les ecritures de tuning : un driver qui quantifierait
            // une demande apparaitrait ici comme un ecart : une information sur ce driver, pas un
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
    /// Le revert d'une ecriture au profil d'un jeu qui ne possedait pas le
    /// reglage.
    ///
    /// La preuve est la provenance relue, en session neuve. Relire la valeur seule ne prouve
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
