namespace DrDxt.Measurement.Memory;

/// <summary>
/// 26/09/2026 : la phrase de la mesure mémoire de l'écran (débit, latence, date, et la réserve d'une référence
/// prise pendant une partie), typée pour entrer dans le rapport de diagnostic. Ce rapport refuse les chaînes
/// libres, par où une clé pourrait arriver ; même motif que <c>LigneReseau</c>.
/// </summary>
public sealed record LigneMemoireMesuree(string Phrase);
