using static DrDxt.Core.TexteFrancais;
namespace DrDxt.Core;

/// <summary>Ce que le joueur cherche. Il n'existe pas de meilleur PC absolu : il existe un
/// meilleur profil pour un objectif.</summary>
public enum OptimizationGoal
{
    /// <summary>Fluidite et regularite avant tout. Le seul objectif reellement implemente
    /// aujourd'hui — les autres viendront avec leurs propres poids, et pas avant.</summary>
    Competitive,
}

/// <summary>Ce qu'une passe de jeu a rendu. <c>null</c> = pas mesure, jamais zero.</summary>
/// <param name="AvgFps">La moyenne. Ce que tout le monde regarde, et ce qui trompe le plus.</param>
/// <param name="P1LowFps">Le 1 % low : ce que le joueur RESSENT quand ca accroche.</param>
/// <param name="P01LowFps">Le 0,1 % low. **Informatif seulement, jamais decisionnel.** Mesure du
/// 07/09/2026 sur le corpus des 8 captures Warzone : ecart p95 observe de 21 % sous no-op, et
/// sous un effet reel de +2 % il rendait une mediane de −3,2 % — il se trompe de SENS plus souvent
/// qu'il ne voit juste. Sur une passe de 10 s a 140 fps il ne repose que sur ~2 frames.</param>
/// <param name="P95FrametimeMs">Le p95 des frametimes, en MILLISECONDES (plus petit = mieux). La
/// grandeur de queue qui resout comme la moyenne — voir <see cref="GameMetricSet"/>.</param>
public sealed record GameMetrics(
    double AvgFps,
    double P1LowFps,
    double? P01LowFps = null,
    double? P95FrametimeMs = null);

/// <summary>
/// La fonction objectif — simple, deterministe, et explicable a voix haute.
///
/// Un autopilote qui optimise la moyenne optimise la mauvaise chose. Un joueur competitif
/// prefere sans hesiter **+0,5 % de moyenne avec +7 % de 1 % low** a **+1,5 % de moyenne avec
/// −3 % de 1 % low** : le premier profil est fluide, le second accroche. La moyenne ne dit rien
/// des accrocs, et les accrocs sont ce qu'on ressent.
///
/// D'ou une ponderation ou **le 1 % low peut battre la moyenne** :
///
///   score = 0,35 × moyenne + 0,65 × 1 % low
///
/// Sur l'exemple ci-dessus : le candidat A marque −1,43, le candidat B marque +4,73. B gagne, et
/// c'est le bon choix.
///
/// Pas de ML, pas de score magique. Deux poids, une somme, et une explication qui tient en une
/// phrase — parce que l'UI devra la dire au joueur, et qu'un score qu'on ne peut pas expliquer ne
/// se conteste pas.
///
/// Les poids sont **NON CHIFFRÉS** : ils encodent une preference, pas une mesure. 0,65/0,35
/// place la fluidite devant le debit sans l'ecraser. Ils sont ici, nommes, pour etre discutes —
/// pas dilues dans une formule.
/// </summary>
public static class GameObjective
{
    /// <summary>Part de la moyenne dans le score Competitive.</summary>
    public const double PoidsMoyenne = 0.35;

    /// <summary>Part du 1 % low. Superieure a celle de la moyenne, et c'est tout l'enjeu.</summary>
    public const double PoidsUnPourCentLow = 0.65;

    /// <summary>Le score d'une passe, en FPS ponderes. La valeur absolue n'a pas de sens en soi ;
    /// ce sont ses ECARTS qui decident, et ils se comparent a une resolution mesuree.</summary>
    public static double Score(GameMetrics m, OptimizationGoal but = OptimizationGoal.Competitive)
        => but switch
        {
            OptimizationGoal.Competitive => PoidsMoyenne * m.AvgFps + PoidsUnPourCentLow * m.P1LowFps,
            _ => PoidsMoyenne * m.AvgFps + PoidsUnPourCentLow * m.P1LowFps,
        };

    /// <summary>Une phrase pour l'ecran. L'objectif doit etre lisible par le joueur, sinon le
    /// verdict n'est qu'une autorite de plus.</summary>
    public static string Explication(OptimizationGoal but) => but switch
    {
        OptimizationGoal.Competitive =>
            F($"fluidite d'abord : {PoidsUnPourCentLow:P0} pour le 1 % low, {PoidsMoyenne:P0} pour la moyenne"),
        _ => "fluidite d'abord",
    };
}
