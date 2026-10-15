using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Coutures matérielles injectables des leviers de réparation (parcage des CCD X3D,
// plan d'alimentation sain).
//
// Comme les coutures de latence et de GPU, elles ne font que lire et dire ce qui est
// possible : elles n'écrivent jamais. Le changement passe toujours par IElevatedClient avec
// un PrivilegedOp typé et sur liste blanche (Phase A). Elles permettent à chaque réparation
// de relever le vrai état d'origine avant la pose (pour un retour arrière fidèle) et de
// décider si elle s'applique.
//
// Chaque méthode rend un Result : une API indisponible échoue proprement
// (TWEAK.SDK_UNAVAILABLE), sans jamais lever. Les implémentations vivent dans
// Detection / Infrastructure ; les tests unitaires utilisent des doublures NSubstitute.
// -----------------------------------------------------------------------

/// <summary>Couture de lecture du plan d'alimentation actif de Windows (PowerGetActiveScheme).
/// La pose passe par IElevatedClient (SetActivePowerPlanOp) ; ceci ne lit que le plan courant,
/// pour que la réparation du plan relève le vrai GUID d'origine et le rende fidèlement.</summary>
public interface IPowerPlanReader
{
    /// <summary>Lit le GUID du plan d'alimentation actif. Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Guid> GetActivePowerPlan();

    /// <summary>PERFBOOSTMODE sur secteur dans ce plan, et le défaut de sa personnalité. Par défaut :
    /// non lu (un lecteur qui ne sait pas le lire le dit, il ne rend jamais un zéro).</summary>
    Result<ModeDeBoostLu> LireModeDeBoost(Guid plan) =>
        Result<ModeDeBoostLu>.Fail(ErrorCodes.TweakSdkUnavailable, "Boost mode is not readable here.");

    /// <summary>L'économie d'énergie de la carte sans fil (secteur) dans ce plan. Par défaut : non lu.</summary>
    Result<EconomieSansFilLue> LireEconomieSansFil(Guid plan) =>
        Result<EconomieSansFilLue>.Fail(ErrorCodes.TweakSdkUnavailable, "Wireless power saving mode is not readable here.");
}

/// <summary>La limite de puissance du processeur Intel (registres MSR par PawnIO), brute et décodée.
/// Par défaut : non lue (un lecteur qui ne sait pas la lire le dit, il ne rend jamais un chiffre).</summary>
public interface ICpuPowerLimitReader
{
    Result<LectureLimiteCpu> LireRegistres() =>
        Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit is not readable here.");
}

/// <summary>L'état courant d'un service Windows, pour que la réparation X3D relève le vrai
/// état d'origine du service AMD 3D V-Cache Performance Optimizer.</summary>
public enum ServiceState
{
    /// <summary>Le service n'est pas installé / absent de la machine.</summary>
    NotInstalled = 0,
    /// <summary>Le service est installé mais arrêté.</summary>
    Stopped = 1,
    /// <summary>Le service est installé et tourne.</summary>
    Running = 2,
}

/// <summary>Couture de lecture des services Windows pour les réparations. La pose (démarrer /
/// arrêter) passe par IElevatedClient (RunServiceControlOp) ; ceci ne lit que l'état, pour que
/// la réparation X3D relève si le service tournait déjà et le rende fidèlement.</summary>
public interface IServiceStateReader
{
    /// <summary>Lit l'état courant d'un service nommé. Un service non installé rend
    /// Ok(ServiceState.NotInstalled). Une vraie erreur de requête →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<ServiceState> GetServiceState(string serviceName);
}


/// <summary>Un mode d'affichage, tel que Windows le décrit (EnumDisplaySettings).</summary>
public sealed record ModeEcran(string Peripherique, int Largeur, int Hauteur, int Bpp, int Hz);

/// <summary>
/// Lire et poser le mode d'un écran, côté utilisateur (user32, sans droits). Les retours entiers sont les codes
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

/// <summary>Demander au joueur de garder le nouveau mode. Pas de réponse dans le délai = non : un écran qui
/// n'affiche rien ne se confirme pas, et l'ancien mode revient (comme Windows après 15 s).</summary>
public interface IConfirmationEcran
{
    Task<bool> GarderAsync(int hz, TimeSpan delai);
}
