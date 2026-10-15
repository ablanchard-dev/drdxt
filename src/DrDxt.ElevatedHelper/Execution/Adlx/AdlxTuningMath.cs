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
// Pourquoi seule la plage de limite de puissance vit ici : une limite de puissance est un %
// absolu, une plage fixe [60,100] a donc du sens dans le helper et reprend GpuSafeBounds. L'op
// d'undervolt, elle, porte une MaxFreq absolue (MHz) : la plage prudente en décalage [-200,+100]
// n'a pas de sens sur une valeur absolue ; elle reste dans la couche App (GpuSafeBounds) et le
// helper valide plutôt la cible absolue contre la plage matérielle annoncée par le pilote (voir
// ADLXTuningActions.SetUndervolt).
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

    /// <summary>Vrai si un pourcentage absolu de limite de puissance est dans la plage de règle
    /// [<see cref="MinPowerLimitPct"/>, <see cref="MaxPowerLimitPct"/>].</summary>
    internal static bool IsPowerLimitWithinHardRail(int pct) =>
        pct >= MinPowerLimitPct && pct <= MaxPowerLimitPct;
}
