namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiTuningMath : la règle de bornes des valeurs GPU imposée à la frontière de privilège ou
// en dessous (défense n°1). Gardée dans le helper (pas dans la feuille native) parce qu'elle
// reprend GpuSafeBounds de la couche App ; les conversions d'unités de la recette (MHz↔kHz,
// %↔PCM, le bornage du pilote) vivent avec la frontière native dans DrDxt.GpuNative
// (NvApiConvert), ce fichier n'est que la règle. Pur, testable sans GPU.
//
// Le helper élevé impose la même plage prudente que GpuSafeBounds : il ne fait jamais confiance
// à l'appelant (ni au bornage côté client) pour les bornes. Un décalage hors plage est rejeté
// (par OpAllowlist et par l'écriture de l'exécuteur dans NvApiTuningActions), jamais borné en
// silence : une op IPC brute (un smoke test tools/r2*, ou une future version de l'App qui branche
// les coutures de réglage) ne peut jamais pousser un décalage hors règle sur le vrai matériel.
//
// GpuSafeBounds (DrDxt.Application) est la source unique de la plage. Le helper ne dépend que de
// DrDxt.Core et ne peut pas la référencer : les constantes sont donc reprises ici ; le test
// inter-assemblages NvApiTuningPolicyBoundSyncTests vérifie qu'elles restent égales à
// GpuSafeBounds.{Min,Max}{Core,Memory}OffsetMhz. Les garder synchronisées.
//
// Pur et total : les vérifications ne lèvent jamais ; pas de Math.Abs (int.MinValue est sûr) et
// NaN / Infinity sont rejetés par le garde-fou de puissance.
// -----------------------------------------------------------------------
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
