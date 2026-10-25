using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// BootLoopGuard (plan T27b) : la sécurité anti-boucle de démarrage.
//
// Un réglage GPU persisté mais devenu mauvais (un décalage d'horloge / de puissance qui fait
// planter le pilote au démarrage) pourrait enfermer la machine dans une boucle si le
// réconciliateur le réimpose à chaque démarrage. La volatilité de NVAPI (un redémarrage revient
// à l'origine) ne protège plus dès que le réconciliateur le réapplique à chaque démarrage.
//
// Donc : si la série de démarrages non propres consécutifs atteint un seuil, la garde suspend la
// réapplication des réglages GPU (marqués par constructeur dans la politique) et le signale. Les
// réglages sûrs côté OS (registre / plan d'alimentation / service / tâche) continuent d'être
// réconciliés : seule la classe des réglages GPU est suspendue.
//
// Logique de décision pure sur un IBootOutcomeStore injecté (série et horloge simulées en
// test). L'hôte marque le démarrage commencé / stable ; ici, la garde ne fait que lire la série
// et décider.
// -----------------------------------------------------------------------
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

    /// <summary>Vrai si et seulement si la réapplication des réglages GPU doit être suspendue
    /// maintenant (la série de démarrages non propres atteint le seuil). Un échec de lecture est
    /// traité par prudence comme « suspendre » : faute de prouver que les démarrages sont sains, on
    /// ne réimpose pas un levier GPU qui pourrait causer la boucle (fail-safe, pas fail-open).</summary>
    public async Task<bool> ShouldSuspendGpuReapplyAsync(CancellationToken ct)
    {
        var streak = await _bootStore.GetUncleanStreakAsync(ct);
        if (!streak.IsOk)
            return true; // prudence : démarrages sains non confirmés → suspendre la réapplication GPU.
        return streak.Value >= _threshold;
    }
}
