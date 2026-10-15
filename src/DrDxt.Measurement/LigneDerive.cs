namespace DrDxt.Measurement;

/// <summary>
/// La phrase de dérive des performances de l'écran (le même jeu, sur cette machine, comparé à la
/// première mesure du journal), typée pour entrer dans le rapport de diagnostic. Ce rapport refuse les chaînes
/// libres, par où une clé pourrait arriver ; même motif que <c>LigneReseau</c>.
/// </summary>
public sealed record LigneDerive(string Phrase);
