using DrDxt.Core;

namespace DrDxt.Detection.Display;

// RefreshHeadroom vit dans DrDxt.Core (DetectionContracts) : SettingsState le porte, et un type
// de l'assembly de detection ne peut pas etre reference depuis Core sans inverser la dependance.

/// <summary>
/// Décide si la machine pilote un écran sous la fréquence que sa dalle supporte : une dalle 200 Hz pilotée à 143 Hz
/// jette 28 % des images que le GPU rend déjà.
/// 1. <b>Même résolution seulement.</b> L'appelant passe les modes de la résolution active : une dalle à 200 Hz en
///    1280x1024 et 144 Hz en 1920x1080 est bien réglée à 144 (le reste est un choix de l'utilisateur).
/// 2. <b>Un écart de 1 Hz n'est pas une occasion.</b> Windows annonce un mode 144 Hz comme 143 (vrai timing ~143,98,
///    l'API tronque).
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
