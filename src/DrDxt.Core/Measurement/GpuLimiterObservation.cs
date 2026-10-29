namespace DrDxt.Core;

/// <summary>
/// Les raisons de bridage que NVIDIA publie, telles quelles.
///
/// Valeurs exactes de `nvmlClocksEventReasons` (docs.nvidia.com/deploy/nvml-api). Elles sont
/// figees ici parce qu'un bitmask mal recopie ne leve rien : il rend juste une conclusion fausse
/// avec l'assurance d'une lecture materielle.
/// </summary>
[Flags]
public enum ClocksEventReason : ulong
{
    /// <summary>`nvmlClocksEventReasonNone`. Une vraie valeur : rien ne bride. A ne jamais
    /// confondre avec « pas lisible ».</summary>
    Aucune = 0x0,

    /// <summary>`GpuIdle` : le GPU ne fait rien. En jeu, c'est le signe qu'on mesure autre chose
    /// que du jeu.</summary>
    GpuInactif = 0x1,

    /// <summary>`ApplicationsClocksSetting` : horloges figees par une consigne applicative.</summary>
    ConsigneApplicative = 0x2,

    /// <summary>`SwPowerCap` : plafond de puissance logiciel atteint.</summary>
    PlafondPuissanceLogiciel = 0x4,

    /// <summary>`HwSlowdown` : ralentissement materiel (thermique ou alimentation).</summary>
    RalentissementMateriel = 0x8,

    /// <summary>`SyncBoost` : synchronisation multi-GPU.</summary>
    SyncBoost = 0x10,

    /// <summary>`SwThermalSlowdown` : ralentissement thermique logiciel.</summary>
    ThermiqueLogiciel = 0x20,

    /// <summary>`HwThermalSlowdown` : ralentissement thermique materiel.</summary>
    ThermiqueMateriel = 0x40,

    /// <summary>`HwPowerBrakeSlowdown` : frein d'alimentation materiel.</summary>
    FreinAlimentation = 0x80,

    /// <summary>`DisplayClockSetting` : horloge limitee par l'affichage.</summary>
    ContrainteAffichage = 0x100,
}

/// <summary>Ce que la telemetrie a observe pendant la passe. Aucune de ces valeurs n'est une
/// cause : ce sont des coincidences temporelles, nommees prudemment.</summary>
public enum GpuLimiterObserved
{
    /// <summary>Rien de lisible : pas de sonde, ou le peripherique ne rend pas ces grandeurs.</summary>
    Inconnu,

    /// <summary>Lu, et rien ne bride. Le GPU tourne librement.</summary>
    RienDeBridant,

    /// <summary>Le GPU passe l'essentiel de la frame occupe. Il est le rythme de la scene, ce
    /// qui ne dit pas qu'il est la cause d'un a-coup.</summary>
    GpuSature,

    /// <summary>Un plafond de puissance a ete observe pendant la passe.</summary>
    PlafondPuissanceObserve,

    /// <summary>Un ralentissement thermique a ete observe pendant la passe.</summary>
    PlafondThermiqueObserve,

    /// <summary>La frequence a chute nettement pendant la passe, sans qu'aucune raison publiee ne
    /// l'explique. C'est un fait sans explication, et le dire ainsi vaut mieux que d'en inventer
    /// une.</summary>
    ChuteDeFrequenceObservee,
}

/// <summary>
/// La lecture materielle minimale, sur la fenetre ou la decision est prise.
///
/// Six grandeurs de la meme passe, pour parler du meme instant : frametime (ailleurs), GPU busy, frequence, watts,
/// temperature, raisons de bridage. Correlation, jamais causalite : « un plafond de puissance observe, frequence -12 % »,
/// jamais « la puissance te coute des FPS », qui demanderait de changer la limite et remesurer.
///
/// La frequence et la raison de bridage ne demandent pas NVAPI : `nvmlDeviceGetClockInfo` et
/// `nvmlDeviceGetCurrentClocksEventReasons` sont des exports NVML publics et documentes (source :
/// documentation NVML officielle, lue le 08/09/2026).
/// </summary>
public static class GpuLimiter
{
    /// <summary>Au-dela, le GPU passe l'essentiel de la frame occupe.
    /// **Non chiffré** : reprend le seuil du classifieur de goulot du produit (0,92), pour
    /// qu'il n'existe pas deux definitions de « GPU-bound » qui divergeraient.</summary>
    public const double SeuilGpuSature = 0.92;

    /// <summary>Chute de frequence, en part de la frequence mediane, au-dela de laquelle on la
    /// nomme. **Non chiffré** : 10 % est assez grand pour ne pas se declencher sur le boost
    /// opportuniste ordinaire d'une carte NVIDIA, assez petit pour attraper un decrochage.</summary>
    public const double ChuteFrequenceNotable = 0.10;

    /// <summary>Ce que ces lectures autorisent a dire, et rien de plus.</summary>
    /// <param name="raisons">Le bitmask publie, ou <c>null</c> si non lisible. <c>0</c> est une
    /// vraie valeur (rien ne bride) : ne pas la confondre avec l'absence de lecture.</param>
    /// <param name="gpuBusyRatio">Occupation GPU dans [0, 1], ou <c>null</c>.</param>
    /// <param name="frequenceMedianeMhz">Frequence mediane de la passe, ou <c>null</c>.</param>
    /// <param name="frequenceMinimaleMhz">Frequence minimale de la passe, ou <c>null</c>.</param>
    public static GpuLimiterObserved Classer(
        ClocksEventReason? raisons,
        double? gpuBusyRatio,
        double? frequenceMedianeMhz,
        double? frequenceMinimaleMhz)
    {
        // L'ordre compte : une raison publiee par le pilote bat toute inference de notre part.
        if (raisons is { } r)
        {
            if (r.HasFlag(ClocksEventReason.ThermiqueMateriel)
                || r.HasFlag(ClocksEventReason.ThermiqueLogiciel))
                return GpuLimiterObserved.PlafondThermiqueObserve;

            if (r.HasFlag(ClocksEventReason.PlafondPuissanceLogiciel)
                || r.HasFlag(ClocksEventReason.FreinAlimentation))
                return GpuLimiterObserved.PlafondPuissanceObserve;

            // `RalentissementMateriel` seul ne dit pas lequel des deux : le pilote le pose en
            // meme temps que le bit precis quand il le connait. Seul, il reste un ralentissement
            // materiel non attribue, et on ne choisit pas a sa place.
            if (r.HasFlag(ClocksEventReason.RalentissementMateriel))
                return GpuLimiterObserved.ChuteDeFrequenceObservee;
        }

        // Une chute de frequence sans raison publiee est un fait sans explication. Le dire ainsi
        // vaut mieux que d'en inventer une.
        if (frequenceMedianeMhz is > 0 && frequenceMinimaleMhz is { } min
            && min < frequenceMedianeMhz.Value * (1 - ChuteFrequenceNotable))
            return GpuLimiterObserved.ChuteDeFrequenceObservee;

        if (gpuBusyRatio is { } busy && busy >= SeuilGpuSature)
            return GpuLimiterObserved.GpuSature;

        // On n'a rien lu du tout : ni raison, ni frequence, ni occupation.
        if (raisons is null && gpuBusyRatio is null && frequenceMedianeMhz is null)
            return GpuLimiterObserved.Inconnu;

        // Une raison lue, ou une frequence lue et stable : « rien ne bride » s'appuie sur l'une ou l'autre. L'occupation seule
        // (carte AMD, NVML muet) ne dit rien du bridage.
        return raisons is not null || frequenceMedianeMhz is > 0
            ? GpuLimiterObserved.RienDeBridant
            : GpuLimiterObserved.Inconnu;
    }

    /// <summary>La phrase du produit. Prudente par construction : elle dit ce qui a ete observe
    /// pendant les fenetres mesurees, jamais ce qui « cause » quoi que ce soit.</summary>
    public static string Phrase(GpuLimiterObserved o) => o switch
    {
        GpuLimiterObserved.RienDeBridant => Docteur.T("GL.Rien"),
        GpuLimiterObserved.GpuSature => Docteur.T("GL.Sature"),
        GpuLimiterObserved.PlafondPuissanceObserve => Docteur.T("GL.Puissance"),
        GpuLimiterObserved.PlafondThermiqueObserve => Docteur.T("GL.Thermique"),
        GpuLimiterObserved.ChuteDeFrequenceObservee => Docteur.T("GL.Chute"),
        _ => Docteur.T("GL.NonLu"),
    };
}
