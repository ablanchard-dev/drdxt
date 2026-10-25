namespace DrDxt.Core;

/// <summary>Ce que le joueur cherche. Il n'existe pas de meilleur PC absolu : il existe un
/// meilleur profil pour un objectif.</summary>
public enum OptimizationGoal
{
    /// <summary>Fluidite et regularite avant tout. Le seul objectif reellement implemente
    /// aujourd'hui : les autres viendront avec leurs propres poids, et pas avant.</summary>
    Competitive,
}

/// <summary>Ce qu'une passe de jeu a rendu. <c>null</c> = pas mesure, jamais zero.</summary>
/// <param name="AvgFps">La moyenne. Ce que tout le monde regarde, et ce qui trompe le plus.</param>
/// <param name="P1LowFps">Le 1 % low : ce que le joueur ressent quand ca accroche.</param>
/// <param name="P01LowFps">Le 0,1 % low. **Informatif seulement, jamais decisionnel.** Mesure du
/// 07/09/2026 sur le corpus des 8 captures Warzone : ecart p95 observe de 21 % sous no-op, et
/// sous un effet reel de +2 % il rendait une mediane de −3,2 % : il se trompe de sens plus souvent
/// qu'il ne voit juste. Sur une passe de 10 s a 140 fps il ne repose que sur ~2 frames.</param>
/// <param name="P95FrametimeMs">Le p95 des frametimes, en millisecondes (plus petit = mieux). La
/// grandeur de queue qui resout comme la moyenne : voir <see cref="GameMetricSet"/>.</param>
public sealed record GameMetrics(
    double AvgFps,
    double P1LowFps,
    double? P01LowFps = null,
    double? P95FrametimeMs = null);
