using DrDxt.Core;

namespace DrDxt.Detection.Display;

// RefreshHeadroom vit dans DrDxt.Core (DetectionContracts) : SettingsState le porte, et un type
// de l'assembly de detection ne peut pas etre reference depuis Core sans inverser la dependance.

/// <summary>
/// Décide si la machine pilote un écran sous la fréquence que sa dalle supporte : le gain
/// gratuit le moins cher d'un PC de jeu, qu'aucun autre levier ne compense. Une dalle 200 Hz
/// pilotée à 143 Hz jette 28 % des images que le GPU rend déjà.
///
/// Deux règles le rendent honnête et pas seulement plausible :
///
/// 1. <b>Même résolution seulement.</b> L'appelant doit passer les modes offerts à la résolution
///    active. Une dalle qui offre 200 Hz en 1280x1024 et 144 Hz en 1920x1080 est bien réglée à
///    144 ; conseiller 200 là échangerait en silence de la résolution contre de la fréquence,
///    une décision de l'utilisateur, pas une optimisation.
///
/// 2. <b>Un écart de 1 Hz n'est pas une occasion.</b> Windows annonce un mode 144 Hz comme 143
///    (le vrai timing est ~143,98 et l'API tronque). Y voir une marge ferait harceler DrDXT sur
///    un écran déjà au maximum.
/// </summary>
public static class DisplayRefreshHeuristic
{
    /// <summary>Écart sous lequel deux modes sont le même mode. Voir la règle 2 ci-dessus.</summary>
    public const int ToleranceHz = 1;

    public static RefreshHeadroom Evaluate(int currentHz, IReadOnlyList<int>? availableHzAtSameResolution)
    {
        if (currentHz <= 0 || availableHzAtSameResolution is null || availableHzAtSameResolution.Count == 0)
        {
            return new RefreshHeadroom(TriState.Unknown, currentHz, 0);
        }

        var best = currentHz;
        foreach (var hz in availableHzAtSameResolution)
        {
            if (hz > best) best = hz;
        }

        var headroom = best - currentHz > ToleranceHz ? TriState.On : TriState.Off;
        return new RefreshHeadroom(headroom, currentHz, best);
    }
}
