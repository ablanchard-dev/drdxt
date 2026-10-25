using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>La forme de la limite de puissance qu'une carte AMD annonce par sa plage (GetPowerLimitRange).</summary>
public enum FormeDeLaLimiteAmd
{
    /// <summary>Ni l'une ni l'autre : rien ne s'écrit.</summary>
    Inconnue,
    /// <summary>Un % du TDP : la plage contient 100.</summary>
    Absolue,
    /// <summary>Un décalage autour de l'usine : la plage contient 0 et descend sous 0.</summary>
    Decalage,
}

/// <summary>
/// La limite de puissance d'une carte AMD. ADLX la dit « en % » (IGPUManualPowerTuning.h,
/// https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUManualPowerTuning.h) sans dire où est l'usine ;
/// AMD documente un curseur « Power Limit (%) » par pas de 1 % (https://www.amd.com/en/resources/support-articles/faqs/DH3-020.html)
/// que les joueurs manient en décalage autour de 0. La plage lue sur la carte tranche : elle contient 0 et descend sous 0
/// (décalage), ou elle contient 100 (absolu) ; sinon, rien ne s'écrit. La cible reste dans la bande de règle
/// (<see cref="GpuSafeBounds"/>), ne monte jamais, et une origine au-dessus de l'usine n'est pas touchée : son retour sortirait
/// de la bande que le helper admet.
/// </summary>
public static class LimitePuissanceAmd
{
    public static FormeDeLaLimiteAmd Forme(PlageDePuissanceAmd plage) =>
        plage.Min > plage.Max ? FormeDeLaLimiteAmd.Inconnue
        : plage.Min < 0 && plage.Max >= 0 ? FormeDeLaLimiteAmd.Decalage
        : plage.Min > 0 && plage.Min <= 100 && plage.Max >= 100 ? FormeDeLaLimiteAmd.Absolue
        : FormeDeLaLimiteAmd.Inconnue;

    /// <summary>La valeur à écrire pour viser <paramref name="pctVoulu"/> % du TDP, dans l'unité de la carte ; null quand
    /// la forme est inconnue, que l'origine est hors bande, ou qu'il n'y a rien à baisser.</summary>
    public static int? Cible(PlageDePuissanceAmd plage, int actuelle, int pctVoulu)
    {
        var pct = GpuSafeBounds.ClampPowerLimitPct(pctVoulu);
        var (valeur, usine) = Forme(plage) switch
        {
            FormeDeLaLimiteAmd.Decalage => (pct - 100, 0),
            FormeDeLaLimiteAmd.Absolue => (pct, 100),
            _ => (default(int?), 0),
        };
        if (valeur is not { } v || actuelle > usine) return null;
        var borne = Math.Max(v, plage.Min);
        return borne < actuelle ? borne : null;
    }

    /// <summary>Une carte AMD de cette machine a son architecture validée par un rapport (<see cref="GpuTuningCoverage"/>).</summary>
    public static bool AmdValidee(HardwareInventory hw, IReadOnlySet<GpuArchitecture> validees) =>
        GpuTuningCoverage.For(hw, validees).Any(r => r.Support == GpuTuningSupport.Validated
            && r.Architecture is GpuArchitecture.Rdna2 or GpuArchitecture.Rdna3 or GpuArchitecture.Rdna4);
}
