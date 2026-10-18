namespace DrDxt.App.ViewModels;

/// <summary>
/// Un composant matériel ou système affiché dans le panneau d'aide (« Le scan va examiner... »
/// ou l'inventaire détecté). Value vaut un tiret cadratin quand il manque.
/// </summary>
/// <param name="IconKey">Clé stable du glyphe à dessiner (Cpu/Gpu/Ram/Ssd/Windows).</param>
/// <param name="Label">Libellé traduit du composant (par exemple « Processeur »).</param>
/// <param name="Value">Modèle ou valeur détecté, ou un tiret cadratin quand il manque.</param>
public sealed record ComponentItem(string IconKey, string Label, string Value)
{
    /// <summary>Le nom que le lecteur d'écran annonce pour l'élément de liste : WPF lit ToString quand l'élément n'a pas de nom.</summary>
    public override string ToString() => $"{Label}{DrDxt.Core.Docteur.DeuxPoints}{Value}";
}

/// <summary>Une ligne du tableau des changements pas encore jugés en jeu.</summary>
public sealed record LigneEnAttente(string Changement, string Date, string EnJeu)
{
    /// <summary>Le nom que le lecteur d'écran annonce pour l'élément de liste : WPF lit ToString quand l'élément n'a pas de nom.</summary>
    public override string ToString() => $"{Changement}, {Date}, {EnJeu}";
}

/// <summary>Un satellite de l'orbite, son composant et le domaine qu'il ouvre.
/// Avant le scan, <see cref="Domaine"/> est nul : pas d'état à dire, pas de clic.</summary>
public sealed record SatelliteDomaine(ComponentItem Composant, DomaineMachine? Domaine)
{
    public bool ADomaine => Domaine is not null;

    /// <summary>Ce que le lecteur d'écran annonce pour l'élément de liste (WPF lit ToString quand l'élément n'a pas de
    /// nom) : le composant, pas la liste des champs du record.</summary>
    public override string ToString() => $"{Composant.Label}{DrDxt.Core.Docteur.DeuxPoints}{Composant.Value}"
        + (Domaine is { } d ? $", {d.EtatLibelle}" : "");
}
