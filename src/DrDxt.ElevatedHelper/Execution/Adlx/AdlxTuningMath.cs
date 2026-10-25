namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// AdlxTuningMath : la règle pure, testable, de bornes de la limite de puissance, imposée à la
// frontière de privilège ou en dessous pour l'écriture de limite de puissance ADLX (AMD).
//
// Le helper élevé impose la même plage prudente que GpuSafeBounds de la couche App : il ne fait
// jamais confiance à l'appelant (ni au bornage côté client) pour les bornes. Une valeur hors
// plage est rejetée par ADLXTuningActions (refus par défaut, échec propre), jamais bornée en
// silence : une op IPC brute (une future version de l'App qui branche les coutures de réglage, ou
// un appelant compromis) ne peut jamais pousser une valeur hors règle sur le vrai matériel.
//
// Une limite de puissance ADLX est « en % » (IGPUManualPowerTuning.h, GetPowerLimit / SetPowerLimit / GetPowerLimitRange :
// « (in %) » ; https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUManualPowerTuning.h), sans dire si
// ce % est absolu ou un décalage autour de l'usine. AMD documente un curseur « Power Limit (%) » qui baisse ou monte la
// limite par pas de 1 % (https://www.amd.com/en/resources/support-articles/faqs/DH3-020.html,
// https://www.amd.com/en/resources/support-articles/faqs/DH-026.html), sans dire où est l'usine ; les joueurs rapportent un
// décalage (0 = usine, -10 = 10 % de moins), aucun document AMD ne le confirme, et l'exemple officiel ManualPowerTuning
// choisit sa valeur dans la plage lue sans la rapporter à 100. Les deux formes sont admises, chacune dans la même bande :
// [60,100] en absolu, [-40,0] en décalage. Elles ne se recouvrent pas, et le pilote valide encore la valeur contre sa
// propre plage : une valeur d'une forme est refusée par une carte de l'autre. L'op
// d'undervolt porte une MaxFreq absolue (MHz) : la plage prudente en décalage [-200,+100] s'applique
// autour de la MaxFreq que le helper a lue à son premier passage depuis le démarrage (voir
// ADLXTuningActions.SetUndervolt), en plus de la plage matérielle annoncée par le pilote.
//
// GpuSafeBounds (DrDxt.Application) est la source unique de la plage en %. Le helper ne dépend
// que de DrDxt.Core (+ la feuille DrDxt.GpuNative) et ne peut pas référencer DrDxt.Application :
// les constantes sont donc reprises ici ; le test inter-assemblages AdlxTuningPolicyBoundSyncTests
// vérifie qu'elles restent égales à GpuSafeBounds. Les garder synchronisées. Pur et total : la
// vérification ne lève jamais.
// -----------------------------------------------------------------------
internal static class AdlxTuningMath
{
    // Reprise de GpuSafeBounds.MinPowerLimitPct / MaxPowerLimitPct (la plage prudente de limite
    // de puissance absolue : ne jamais affamer le GPU sous le plancher).
    internal const int MinPowerLimitPct = 60;
    internal const int MaxPowerLimitPct = 100;

    // La même bande, dite en décalage autour de l'usine (100 %) : jamais au-dessus de l'usine, jamais sous le plancher.
    internal const int MinPowerLimitOffset = MinPowerLimitPct - 100;
    internal const int MaxPowerLimitOffset = MaxPowerLimitPct - 100;

    // Reprise de GpuSafeBounds.MinCoreOffsetMhz / MaxCoreOffsetMhz : la MaxFreq absolue de l'op ne s'écarte pas de plus que
    // cette bande de la MaxFreq que le helper a lue à son premier passage depuis le démarrage (ADLXTuningActions.SetUndervolt).
    internal const int MinCoreOffsetMhz = -200;
    internal const int MaxCoreOffsetMhz = 100;

    /// <summary>Vrai si une limite de puissance est dans la bande de règle, en absolu
    /// [<see cref="MinPowerLimitPct"/>, <see cref="MaxPowerLimitPct"/>] ou en décalage
    /// [<see cref="MinPowerLimitOffset"/>, <see cref="MaxPowerLimitOffset"/>].</summary>
    internal static bool IsPowerLimitWithinHardRail(int pct) =>
        pct is >= MinPowerLimitPct and <= MaxPowerLimitPct or >= MinPowerLimitOffset and <= MaxPowerLimitOffset;
}
