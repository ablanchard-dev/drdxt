namespace DrDxt.App.ViewModels;

/// <summary>
/// L'une des quatre cartes de présentation affichées sur l'écran d'attente
/// (Diagnostic / Gaming / Security / Report). Inerte pour l'instant (« En savoir plus » → à venir).
/// </summary>
/// <param name="IconKey">Clé stable du glyphe.</param>
/// <param name="Title">Titre traduit de la carte.</param>
/// <param name="Body">Description traduite, en une ligne.</param>
public sealed record FeatureItem(string IconKey, string Title, string Body);
