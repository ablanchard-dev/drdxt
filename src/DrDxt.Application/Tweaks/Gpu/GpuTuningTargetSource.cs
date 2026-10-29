using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// IGpuTuningTargetSource : le contexte GPU que les leviers GPU lisent à la pose. Un profil de puce (DrDxt.Profiles)
// fournit les cibles, le scan les pose par Set() bornées à GpuSafeBounds. La source n'autorise rien : chaque levier
// reborne par GpuSafeBounds avant toute écriture privilégiée (« un profil est une demande, pas une autorisation »).
// Elle porte aussi le fait « un GPU AMD est présent » (GpuGating.IsAmd sur le HardwareInventory scanné, fabricant DXGI
// 0x1002), connu même sans le SDK ADLX : « GPU AMD détecté, réglage à venir » plutôt qu'un saut silencieux. Ici et
// pas sur la couture ADLX, dont l'implémentation de prod dit « indisponible » partout. Amorcée avec des défauts prudents
// dans les bornes ; Reset() y revient et efface le drapeau, pour qu'un scan ne fuie jamais dans le suivant.
public interface IGpuTuningTargetSource
{
    /// <summary>La cible courante de décalage d'horloge cœur/mémoire que pose le levier de sous-tension.</summary>
    GpuTuningTarget Tuning { get; }

    /// <summary>La cible courante de limite de puissance que pose le levier de limite de puissance.</summary>
    GpuPowerLimitTarget PowerLimit { get; }

    /// <summary>Vrai quand un GPU AMD a été détecté dans le matériel scanné, ADLX disponible ou non. Faux tant qu'il n'est
    /// pas posé depuis le matériel.</summary>
    bool IsAmdGpuPresent { get; }

    /// <summary>Une carte AMD de la machine scannée a son architecture validée par un rapport : sans lui, aucune écriture
    /// AMD de limite de puissance, même appelée directement.</summary>
    bool AmdEcritureValidee => false;

    /// <summary>Remplace les cibles en cours (l'appelant doit les avoir bornées à GpuSafeBounds).</summary>
    void Set(GpuTuningTarget tuning, GpuPowerLimitTarget powerLimit);

    /// <summary>Note si un GPU AMD est présent, calculé ici depuis le matériel scanné
    /// (hw.Gpus via GpuGating.IsAmd). Appelé au scan, avant la pose de tout levier GPU.
    /// Inventaire null ou vide → absent (refus au moindre doute).</summary>
    void SetAmdGpuPresenceFromHardware(HardwareInventory hw);

    /// <summary>Remet les défauts prudents d'amorce et efface le drapeau de présence
    /// (aucun remplacement de profil actif, aucun fait matériel périmé).</summary>
    void Reset();
}

public sealed class GpuTuningTargetSource : IGpuTuningTargetSource
{
    private readonly GpuTuningTarget _defaultTuning;
    private readonly GpuPowerLimitTarget _defaultPowerLimit;

    public GpuTuningTargetSource(GpuTuningTarget defaultTuning, GpuPowerLimitTarget defaultPowerLimit)
    {
        _defaultTuning = defaultTuning ?? throw new ArgumentNullException(nameof(defaultTuning));
        _defaultPowerLimit = defaultPowerLimit ?? throw new ArgumentNullException(nameof(defaultPowerLimit));
        Tuning = _defaultTuning;
        PowerLimit = _defaultPowerLimit;
    }

    public GpuTuningTarget Tuning { get; private set; }

    public GpuPowerLimitTarget PowerLimit { get; private set; }

    public bool IsAmdGpuPresent { get; private set; }

    public bool AmdEcritureValidee { get; private set; }

    public void Set(GpuTuningTarget tuning, GpuPowerLimitTarget powerLimit)
    {
        Tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        PowerLimit = powerLimit ?? throw new ArgumentNullException(nameof(powerLimit));
    }

    public void SetAmdGpuPresenceFromHardware(HardwareInventory hw) => SetAmdGpuPresenceFromHardware(hw, GpuTuningCoverage.ValideesEnVie);

    /// <param name="validees">Les architectures validées ; <see cref="GpuTuningCoverage.ValideesEnVie"/> en vie.</param>
    public void SetAmdGpuPresenceFromHardware(HardwareInventory hw, IReadOnlySet<GpuArchitecture> validees)
    {
        IsAmdGpuPresent = hw?.Gpus is { } gpus && gpus.Any(GpuGating.IsAmd);
        AmdEcritureValidee = IsAmdGpuPresent && LimitePuissanceAmd.AmdValidee(hw!, validees);
    }

    public void Reset()
    {
        Tuning = _defaultTuning;
        PowerLimit = _defaultPowerLimit;
        IsAmdGpuPresent = false;
        AmdEcritureValidee = false;
    }
}
