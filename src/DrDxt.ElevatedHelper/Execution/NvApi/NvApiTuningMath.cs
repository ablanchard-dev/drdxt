namespace DrDxt.ElevatedHelper.Execution.NvApi;

// NvApiTuningMath : la règle de bornes des valeurs GPU à la frontière de privilège (défense n°1), dans le helper parce
// qu'elle reprend GpuSafeBounds ; les conversions d'unités (MHz↔kHz, %↔PCM, bornage du pilote) vivent dans
// DrDxt.GpuNative (NvApiConvert). Le helper ne fait jamais confiance au bornage de l'appelant : un décalage hors plage
// est rejeté (OpAllowlist, et l'écriture de l'exécuteur dans NvApiTuningActions), jamais borné en silence ; une op IPC
// brute (smoke test tools/r2*, future App) ne pousse rien hors règle sur le vrai matériel.
// GpuSafeBounds (DrDxt.Application) est la source unique ; le helper ne dépend que de DrDxt.Core, d'où les constantes
// reprises ici, que NvApiTuningPolicyBoundSyncTests garde égales à GpuSafeBounds.{Min,Max}{Core,Memory}OffsetMhz.
// Pur et total : jamais de levée, pas de Math.Abs (int.MinValue est sûr), NaN / Infinity rejetés par le garde-fou.
internal static class NvApiTuningMath
{
    // Reprise de GpuSafeBounds.MinCoreOffsetMhz / MaxCoreOffsetMhz (la plage prudente).
    internal const int MinCoreOffsetMhz = -200;
    internal const int MaxCoreOffsetMhz = 100;

    // Reprise de GpuSafeBounds.MinMemoryOffsetMhz / MaxMemoryOffsetMhz.
    internal const int MinMemoryOffsetMhz = -500;
    internal const int MaxMemoryOffsetMhz = 500;

    // Garde-fou en % de la limite de puissance. Plus large exprès que GpuSafeBounds (60..100 %) : la
    // cible de puissance est aussi bornée 0..100 par OpAllowlist et ramenée par le pilote au [min,max]
    // annoncé du GPU dans NvApiConvert.ClampPcm ; ceci reste donc la seule garde de dernier recours fini/NaN.
    internal const double MinPowerPercent = 50.0;
    internal const double MaxPowerPercent = 120.0;

    /// <summary>Vrai si un décalage d'horloge cœur (MHz) est dans la plage de règle
    /// [<see cref="MinCoreOffsetMhz"/>, <see cref="MaxCoreOffsetMhz"/>]. Sans Math.Abs :
    /// int.MinValue ne peut jamais déborder à travers l'IPC.</summary>
    internal static bool IsCoreOffsetWithinHardRail(int mhz) =>
        mhz >= MinCoreOffsetMhz && mhz <= MaxCoreOffsetMhz;

    /// <summary>Vrai si un décalage d'horloge mémoire (MHz) est dans la plage de règle
    /// [<see cref="MinMemoryOffsetMhz"/>, <see cref="MaxMemoryOffsetMhz"/>].</summary>
    internal static bool IsMemoryOffsetWithinHardRail(int mhz) =>
        mhz >= MinMemoryOffsetMhz && mhz <= MaxMemoryOffsetMhz;

    /// <summary>Vrai si un pourcentage de limite de puissance est fini et dans le garde-fou strict du helper.
    /// NaN / Infinity sont rejetés (ils passeraient à travers des comparaisons naïves &lt;/&gt;).</summary>
    internal static bool IsPowerPercentWithinHardRail(double percent) =>
        double.IsFinite(percent) && percent >= MinPowerPercent && percent <= MaxPowerPercent;
}
