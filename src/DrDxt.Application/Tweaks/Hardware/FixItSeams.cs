using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Injectable hardware seams for the M2 Phase F "fix-it" tweaks (F1 X3D CCD
// parking, F2 stale driver, F3 sane power plan).
//
// Like the latency / GPU seams, these are read + CAPABILITY seams only — they
// Never perform the privileged write. The change is always applied through
// IElevatedClient with a typed, allowlisted PrivilegedOp (Phase A). The seams let
// each fix-it tweak capture the real original state before applying (for a
// faithful revert) and decide gating.
//
// Every method returns a Result so an unavailable API fails CLEAN
// (TWEAK.SDK_UNAVAILABLE) — it must never throw. Implementations live in
// Detection / Infrastructure; unit tests use NSubstitute fakes.
// -----------------------------------------------------------------------

/// <summary>Read seam over the active Windows power plan (PowerGetActiveScheme).
/// Apply is done via IElevatedClient (SetActivePowerPlanOp); this only reads the
/// current plan so F3 can capture the real original GUID for a faithful revert.</summary>
public interface IPowerPlanReader
{
    /// <summary>Reads the currently active power-plan GUID. Unavailable path →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Guid> GetActivePowerPlan();

    /// <summary>07/10/2026 (L-CPU) : PERFBOOSTMODE sur secteur dans ce plan, et le défaut de sa personnalité. Par défaut :
    /// non lu (un lecteur qui ne sait pas le lire le dit, il ne rend jamais un zéro).</summary>
    Result<ModeDeBoostLu> LireModeDeBoost(Guid plan) =>
        Result<ModeDeBoostLu>.Fail(ErrorCodes.TweakSdkUnavailable, "Boost mode is not readable here.");
}

/// <summary>B1 (10/10/2026) : la limite de puissance du processeur Intel (registres MSR par PawnIO), brute et décodée.
/// Par défaut : non lue (un lecteur qui ne sait pas la lire le dit, il ne rend jamais un chiffre).</summary>
public interface ICpuPowerLimitReader
{
    Result<LectureLimiteCpu> LireRegistres() =>
        Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit is not readable here.");
}

/// <summary>The current state of a Windows service, used by F1 to capture the real
/// original state of the AMD 3D V-Cache Performance Optimizer service.</summary>
public enum ServiceState
{
    /// <summary>The service is not installed / not present on the machine.</summary>
    NotInstalled = 0,
    /// <summary>The service is installed but stopped.</summary>
    Stopped = 1,
    /// <summary>The service is installed and running.</summary>
    Running = 2,
}

/// <summary>Read seam over Windows services for the fix-it tweaks. Apply (start /
/// stop) is done via IElevatedClient (RunServiceControlOp); this only reads state
/// so F1 can capture whether the service was already running for a faithful revert.</summary>
public interface IServiceStateReader
{
    /// <summary>Reads the current state of a named service. A service that is not
    /// installed returns Ok(ServiceState.NotInstalled). A genuine query error →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<ServiceState> GetServiceState(string serviceName);
}


/// <summary>L-ECRAN : un mode d'affichage, tel que Windows le décrit (EnumDisplaySettings).</summary>
public sealed record ModeEcran(string Peripherique, int Largeur, int Hauteur, int Bpp, int Hz);

/// <summary>
/// L-ECRAN : lire et poser le mode d'un écran, côté utilisateur (user32, sans droits). Les retours entiers sont les codes
/// DISP_CHANGE de ChangeDisplaySettingsEx : 0 = réussi, tout autre = refusé.
/// </summary>
public interface IModeEcran
{
    /// <summary>Le mode courant (ENUM_CURRENT_SETTINGS) ; null = non lu.</summary>
    ModeEcran? Courant(string peripherique);

    /// <summary>CDS_TEST : Windows dit si le mode pourrait être posé, sans le poser.</summary>
    int Tester(ModeEcran mode);

    /// <summary>Pose le mode. <paramref name="enregistrer"/> faux = changement dynamique, perdu à la prochaine session ;
    /// vrai = CDS_UPDATEREGISTRY, gardé dans le profil de l'utilisateur.</summary>
    int Ecrire(ModeEcran mode, bool enregistrer);
}

/// <summary>L-ECRAN : demander au joueur de garder le nouveau mode. Pas de réponse dans le délai = non : un écran qui
/// n'affiche rien ne se confirme pas, et l'ancien mode revient (comme Windows après 15 s).</summary>
public interface IConfirmationEcran
{
    Task<bool> GarderAsync(int hz, TimeSpan delai);
}
