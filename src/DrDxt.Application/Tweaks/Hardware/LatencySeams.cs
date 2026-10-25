using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Coutures matérielles injectables des leviers de latence.
//
// Elles ne font que lire et dire ce qui est possible : elles n'écrivent jamais
// elles-mêmes. Le vrai changement passe toujours par IElevatedClient avec un
// PrivilegedOp typé et sur liste blanche (Phase A). Elles existent pour que chaque
// levier puisse :
//   * décider s'il s'applique (capacité / classe d'appareil), et
//   * relever la vraie valeur d'origine avant la pose (pour un retour arrière fidèle).
//
// Chaque méthode rend un Result : un SDK ou un chemin de registre indisponible échoue
// proprement (TWEAK.SDK_UNAVAILABLE), sans jamais lever. Les implémentations vivent dans
// Detection / Infrastructure (P/Invoke) ; les tests unitaires utilisent des doublures NSubstitute.
// -----------------------------------------------------------------------

/// <summary>Couture de lecture du pilote NVIDIA (NVAPI / registre NVCP) pour les leviers
/// de latence. La pose passe par IElevatedClient ; ceci ne fait que lire.</summary>
public interface INvApi
{
    /// <summary>Vrai quand un pilote NVIDIA / NVAPI est présent et utilisable.</summary>
    bool IsAvailable { get; }

    /// <summary>Lit la valeur courante de « Low Latency Mode » du pilote (NVTweak).
    /// 0 = Off, 1 = On, 2 = Ultra. Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetLowLatencyMode();

    /// <summary>Lit la valeur DRS courante « Max Pre-Rendered Frames » pour le plafond de la
    /// file de rendu. Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetMaxPreRenderedFrames();

    /// <summary>Lit la valeur DRS Vsync courante du pilote. Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetVsyncMode();

    /// <summary>Lit la limite d'images courante du pilote (fps). 0 = pas de plafond.
    /// Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetFrameRateLimit();

    /// <summary>Qui a posé le plafond d'images, tel que le pilote le rapporte à la même lecture.
    /// Chemin indisponible -> Fail(TWEAK.SDK_UNAVAILABLE) : « non lu », jamais « personne ne l'a posé ».</summary>
    Result<DrsOriginState> GetFrameRateLimitOrigin();

    /// <summary>Lit la valeur DRS courante « Power management mode » (PREFERRED_PSTATE), pour
    /// que le levier Prefer-Max-Perf relève la vraie origine et la rende fidèlement.
    /// 0 = Adaptive, 1 = Prefer max performance (NvApiDriverSettings.h). Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<uint> GetPreferredPstate();

    /// <summary>Vrai seulement quand PREFERRED_PSTATE est absent du profil de base (NVAPI_SETTING_NOT_FOUND) : il est au
    /// défaut du pilote, OPTIMAL_POWER. Une panne de lecture rend faux. Par défaut : faux, le cas prudent.</summary>
    bool PreferredPstateAuDefaut() => false;

    /// <summary>Un reglage DRS tel que le pilote l'applique a un executable, avec sa
    /// provenance. Le profil de base n'est pas ce que le jeu recoit : un profil de jeu peut porter
    /// sa propre valeur, et ecrire ou relire le global validerait alors un reglage sans effet.
    /// Par defaut : Fail. Une couture qui ne sait pas lire au scope du jeu le dit ; elle ne rend
    /// jamais la valeur globale a la place.</summary>
    Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
        => Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable,
            "This seam cannot read a driver setting at game scope.");

    /// <summary>Le reglage « Configurer G-SYNC » du pilote (DRS VRR_MODE, NVIDIA
    /// NvApiDriverSettings.h) : 0 desactive, 1 plein ecran seulement, 2 plein ecran et fenetre.
    /// Par defaut : Fail, « non lu ».</summary>
    Result<uint> GetGsyncMode()
        => Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the G-SYNC mode.");
}

/// <summary>Le chemin complet de l'executable du jeu en cours, ou null. C'est la cle
/// que le pilote NVIDIA utilise pour choisir le profil qu'il applique ; sans elle, toute lecture
/// DRS decrit le profil global, pas le jeu.</summary>
public interface IGameExecutable
{
    string? RunningGameExecutablePath();

    /// <summary>Les noms des modules chargés par le jeu en cours (d3d11.dll, vulkan-1.dll…), d'où se lit son API graphique.
    /// null : non lus (pas de jeu, accès refusé par le jeu ou son anti-triche).</summary>
    IReadOnlyList<string>? ModulesDuJeu() => null;
}

/// <summary>Couture de lecture du runtime AMD ADLX pour les leviers de latence (Anti-Lag
/// est le plancher AMD de file de rendu / basse latence). La pose passe par IElevatedClient.</summary>
public interface IAdlx
{
    /// <summary>Vrai quand le runtime AMD ADLX est présent et utilisable.</summary>
    bool IsAvailable { get; }

    /// <summary>Lit l'état courant d'Anti-Lag (0 = off, 1 = on). Chemin indisponible
    /// → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetAntiLagState();

    /// <summary>La limite d'images que le pilote AMD impose (FRTC ou Chill), en images/s ;
    /// 0 = aucune. Par défaut : Fail, « non lu ».</summary>
    Result<int> GetFrameRateLimit()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the AMD frame limit.");

    /// <summary>FRTC en un entier (<c>AdlxTuningKind.FrameRateTarget</c>) : allumé = sa cible,
    /// éteint = moins la cible réglée. Par défaut : Fail, « non lu ».</summary>
    Result<int> GetFrameRateTarget()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read AMD FRTC.");

    /// <summary>Le mode Vsync du pilote AMD (0 à 3, <c>AdlxTuningKind.VerticalRefresh</c>).</summary>
    Result<int> GetVsyncMode()
        => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read the AMD Vsync mode.");

    /// <summary>Enhanced Sync est-il allumé ? La Vsync du pilote l'éteint en s'allumant (I3DSettings.h).</summary>
    Result<bool> GetEnhancedSyncActif()
        => Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read AMD Enhanced Sync.");

    /// <summary>Radeon Chill est-il allumé ? Anti-Lag l'éteint en s'allumant (I3DSettings.h :
    /// fonctions exclusives), et l'éteindre ensuite ne le rallume pas. Par défaut : Fail, « non lu ».</summary>
    Result<bool> GetChillActif()
        => Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "This seam cannot read Radeon Chill.");
}

/// <summary>Informations sur un écran, pour la condition VRR et pour le constat
/// « écran piloté sous ses capacités ».</summary>
public sealed record DisplayInfo(
    bool IsPrimaryGameDisplay,
    bool SupportsVrr /* G-Sync / FreeSync capable AND enabled */,
    int RefreshHz,
    // En dernier, avec défaut : les constructions existantes compilent toujours.
    /// <summary>La fréquence la plus haute que l'écran offre à la résolution courante. 0 = non lu.
    /// Jamais remplie par défaut avec <see cref="RefreshHz"/> : « on n'a pas lu le maximum » et
    /// « l'écran est déjà à son maximum » sont deux réponses différentes, et les confondre
    /// cacherait le déficit que ce champ existe pour montrer.</summary>
    int MaxRefreshHz = 0,
    string? DeviceName = null,
    // Le VRR lu au pilote (NVAPI). Unknown = non lu, jamais Off. `SupportsVrr` n'est
    // vrai que si les deux sont On.
    TriState VrrPossible = TriState.Unknown,
    TriState VrrEnabled = TriState.Unknown,
    // La résolution courante et le nom d'interface du moniteur, pour relire son EDID.
    int Largeur = 0,
    int Hauteur = 0,
    string? NomInterface = null,
    // La carte qui pilote cet écran (EnumDisplayDevices), pour voir un écran branché sur la
    // carte mère.
    string? Adaptateur = null)
{
    /// <summary>
    /// L'écran peut faire mieux que ce que Windows lui demande. Arithmétique prouvée, pas une
    /// estimation : le maximum que l'écran offre moins la fréquence à laquelle il tourne.
    /// <para>
    /// Vu sur la machine de référence : un écran capable de 200 Hz piloté à 144. La règle basse
    /// latence de NVIDIA plafonne les images 3 sous la fréquence, d'où 141, et ce 141 était la
    /// seule trace visible du vrai défaut. Corriger le plafond n'aurait rien corrigé ; le
    /// déficit était l'écran, et le plafond son symptôme fidèle.
    /// </para>
    /// Un maximum inconnu ne retire rien, comme le reste de la doctrine de notation.
    /// </summary>
    public bool IsBelowCapability => MaxRefreshHz > 0 && RefreshHz > 0 && MaxRefreshHz > RefreshHz;

    /// <summary>Les images par seconde que l'écran peut afficher et qu'on ne lui demande pas.</summary>
    public int RefreshHeadroomHz => IsBelowCapability ? MaxRefreshHz - RefreshHz : 0;
}

/// <summary>Couture de lecture de la configuration d'affichage. Le levier de plafond VRR/Vsync
/// s'en sert pour savoir si un écran G-Sync/FreeSync est présent avant de poser un plafond
/// basse latence (pas de plafond à l'aveugle sur un écran sans VRR).</summary>
public interface IDisplayInfo
{
    /// <summary>Rend l'écran de jeu principal (celui que vise un jeu plein écran).
    /// Indisponible / ambigu → Fail(TWEAK.SDK_UNAVAILABLE) : le levier passe proprement
    /// au lieu de plafonner le mauvais écran.</summary>
    Result<DisplayInfo> GetPrimaryGameDisplay();

    /// <summary>Tous les écrans branchés au bureau, le principal d'abord.
    /// <para>
    /// Un seul écran ne suffit pas à répondre à la question qui compte sur un bureau à plusieurs
    /// écrans : un second écran plus rapide est invisible pour <see cref="GetPrimaryGameDisplay"/>,
    /// et rien ne pourrait dire que le jeu tourne sur le plus lent.
    /// </para></summary>
    Result<IReadOnlyList<DisplayInfo>> GetAllDisplays();
}

/// <summary>Couture minimale de lecture du registre (aucune écriture : elles passent par le
/// helper élevé). Un levier y relève la vraie valeur DWORD d'origine avant la pose, pour que
/// le snapshot porte une op inverse fidèle. Clé absente → Ok(null) : la valeur n'existe pas
/// encore (son « origine » est l'absence). Chemin de registre cassé →
/// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
public interface IRegistryReader
{
    /// <summary>Lit une valeur de registre DWORD. Rend Ok(null) si la valeur n'existe
    /// pas (relevé = « absente ») ; Fail(TWEAK.SDK_UNAVAILABLE) sur une erreur de lecture.</summary>
    Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name);

    /// <summary>Lit une valeur REG_SZ. Rend Ok(null) si la valeur n'existe pas
    /// (relevé = « absente ») ; Fail(TWEAK.SDK_UNAVAILABLE) sur une erreur de lecture. Sert aux
    /// leviers qui lisent, modifient et réécrivent une chaîne composite (par exemple DirectXUserGlobalSettings).</summary>
    Result<string?> ReadString(RegistryHiveKind hive, string path, string name);

    /// <summary>Lit un REG_MULTI_SZ et rend ses entrees jointes par des sauts de ligne,
    /// la meme forme que celle transportee par SetRegistryValueOp, pour que capture et
    /// rejeu soient symetriques. Ok(null) = valeur absente (capture = « absente ») ;
    /// Fail(TWEAK.SDK_UNAVAILABLE) en cas d'erreur de lecture. Jamais une valeur inventee.</summary>
    Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name);
}

/// <summary>Secteur ou batterie. Un portable sur batterie est souvent réglé exprès pour l'autonomie (écran à 60 Hz) :
/// un levier qui défait ce réglage ne s'y applique pas.</summary>
public interface ISourceDAlimentation
{
    /// <summary>Vrai sur batterie, faux sur secteur, null quand Windows ne le dit pas (bureau sans batterie compris).</summary>
    bool? SurBatterie();
}
