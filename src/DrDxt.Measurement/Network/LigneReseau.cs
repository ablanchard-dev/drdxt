namespace DrDxt.Measurement.Network;

/// <summary>
/// 25/09/2026 : la phrase de la mesure réseau (gigue au premier saut contre chemin complet),
/// typée pour entrer dans le rapport de diagnostic. Ce rapport refuse les chaînes libres, par où
/// une clé pourrait arriver ; même motif que <c>CpuDeficitSoutenu</c>.
/// </summary>
public sealed record LigneReseau(string Phrase);
