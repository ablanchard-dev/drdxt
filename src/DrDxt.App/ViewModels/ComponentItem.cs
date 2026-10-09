namespace DrDxt.App.ViewModels;

/// <summary>
/// A single hardware/system component shown in the assistant panel
/// ("Le scan va examiner..." / detected inventory). Value is "—" when unavailable.
/// </summary>
/// <param name="IconKey">Stable key identifying the glyph to render (Cpu/Gpu/Ram/Ssd/Windows).</param>
/// <param name="Label">Localized component label (e.g. "Processeur").</param>
/// <param name="Value">Detected model/value, or "—" when unavailable.</param>
public sealed record ComponentItem(string IconKey, string Label, string Value);

/// <summary>08/10/2026 (critique UI, défaut 8) : une ligne du tableau des changements pas encore jugés en jeu.</summary>
public sealed record LigneEnAttente(string Changement, string Date, string EnJeu);

/// <summary>08/10/2026 (critique UI, défaut 6) : un satellite de l'orbite, son composant et le domaine qu'il ouvre.
/// Avant le scan, <see cref="Domaine"/> est nul : pas d'état à dire, pas de clic.</summary>
public sealed record SatelliteDomaine(ComponentItem Composant, DomaineMachine? Domaine)
{
    public bool ADomaine => Domaine is not null;
}
