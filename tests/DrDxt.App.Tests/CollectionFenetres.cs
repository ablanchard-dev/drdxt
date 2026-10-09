namespace DrDxt.App.Tests;

/// <summary>
/// Les tests qui lisent ou ouvrent des fenêtres dans la vraie session. L'un d'eux montre un instant une
/// fenêtre de la taille de l'écran avant de la réduire ; un autre, lancé en même temps par xUnit, l'a
/// prise pour le jeu (26/09/2026 : « attendu aucun jeu, trouvé 3096 », un processus enfant déjà terminé
/// quand on l'a cherché). Hors de toute parallélisation, chacun ne voit que ses propres fenêtres.
/// </summary>
[CollectionDefinition(Nom, DisableParallelization = true)]
public sealed class CollectionFenetres
{
    public const string Nom = "Fenêtres de la vraie session";
}
