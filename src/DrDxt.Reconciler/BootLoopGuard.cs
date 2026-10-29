using DrDxt.Core;

namespace DrDxt.Reconciler;

// BootLoopGuard (plan T27b) : la sécurité anti-boucle de démarrage. Un réglage GPU persisté devenu mauvais (un décalage
// d'horloge / de puissance qui fait planter le pilote) enfermerait la machine dans une boucle si le réconciliateur le
// réimposait à chaque démarrage (la volatilité de NVAPI ne protège plus). Au seuil de démarrages non propres consécutifs,
// la garde suspend les réglages GPU (marqués par constructeur dans la politique) et le signale ; registre, plan
// d'alimentation, service et tâche restent réconciliés. Décision pure sur un IBootOutcomeStore injecté ; l'hôte marque
// démarrage commencé / stable.
public sealed class BootLoopGuard
{
    private readonly IBootOutcomeStore _bootStore;
    private readonly int _threshold;

    /// <summary>Le seuil par défaut (démarrages non propres consécutifs à partir desquels la réapplication des
    /// réglages GPU est suspendue) ; aussi la série d'un état mis de côté, qui ne se sait plus.</summary>
    public const int SeuilParDefaut = 3;

    public BootLoopGuard(IBootOutcomeStore bootStore, int threshold = SeuilParDefaut)
    {
        _bootStore = bootStore;
        _threshold = threshold;
    }

    /// <summary>Vrai si et seulement si la série de démarrages non propres atteint le seuil : les réglages GPU sont
    /// suspendus. Un échec de lecture vaut « suspendre » (fail-safe, pas fail-open).</summary>
    public async Task<bool> ShouldSuspendGpuReapplyAsync(CancellationToken ct)
    {
        var streak = await _bootStore.GetUncleanStreakAsync(ct);
        if (!streak.IsOk)
            return true; // prudence : démarrages sains non confirmés → suspendre la réapplication GPU.
        return streak.Value >= _threshold;
    }
}
