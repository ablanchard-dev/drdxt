using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>Un levier applique dont la valeur est retombee sur celle d'avant.</summary>
/// <param name="TweakId">Le levier concerne.</param>
/// <param name="Cible">La feuille relue, pour que le constat soit verifiable a la main.</param>
public sealed record GainDisparu(string TweakId, string? Cible);

/// <summary>
/// Remarquer qu'un gain a disparu, sans jamais le reecrire.
///
/// Les leviers absents du reconciliateur le restent (voir `DefaultReconcilableTweakCatalog` : HKCU y serait la ruche de
/// SYSTEM, et il n'a pas le droit d'ecrire le GPU) ; ce qui manquait, c'est que quelqu'un remarque un gain disparu. Ce
/// type lit la feuille du snapshot du levier, compare, n'ecrit rien, et ne rend que les gains disparus (un
/// `DifferentDeAvant` ne se prouve pas « applique »).
/// </summary>
public static class GainsDisparus
{
    /// <summary>Parmi les leviers appliques, ceux dont la valeur est revenue a l'etat d'avant.</summary>
    /// <param name="appliques">Les snapshots du manifeste : ce que le produit croit avoir applique.</param>
    /// <param name="lecteur">Lecture registre non elevee : lire ne demande pas l'UAC.</param>
    public static IReadOnlyList<GainDisparu> Detecter(
        IEnumerable<TweakSnapshot>? appliques, IRegistryReader lecteur)
    {
        if (appliques is null) return [];

        var perdus = new List<GainDisparu>();
        foreach (var snapshot in appliques)
        {
            // Une origine égale à la valeur posée ne porte aucun changement ; la relire
            // « revenue à l'avant » crierait au gain perdu sur un réglage en place.
            if (snapshot is null || string.Equals(snapshot.AppliedValueJson, snapshot.OriginalValueJson, StringComparison.Ordinal)) continue;
            var relu = TweakReadback.Relire(snapshot, lecteur);

            // Seul `RevenuAvantApplication` compte. `NonLu` et `NonRelisable` sont des trous, et
            // les compter comme des pertes ferait crier le produit sur ce qu'il n'a pas su lire :
            // la faute exacte que ce depot corrige partout ailleurs.
            if (relu.Etat == EtatRelu.RevenuAvantApplication)
                perdus.Add(new GainDisparu(snapshot.TweakId, relu.Cible));
        }

        return perdus;
    }

    /// <summary>Le bilan de la relecture : toujours différents de l'origine (sans prétendre « appliqués », voir
    /// <see cref="EtatRelu.DifferentDeAvant"/>), revenus à la valeur d'avant, et non relus.</summary>
    public static (int Differents, int Revenus, int NonRelus) Bilan(IEnumerable<TweakSnapshot>? appliques, IRegistryReader lecteur)
    {
        var (d, r, n) = (0, 0, 0);
        foreach (var s in appliques ?? [])
        {
            if (s is null || string.Equals(s.AppliedValueJson, s.OriginalValueJson, StringComparison.Ordinal)) continue;
            switch (TweakReadback.Relire(s, lecteur).Etat)
            {
                case EtatRelu.RevenuAvantApplication: r++; break;
                case EtatRelu.DifferentDeAvant: d++; break;
                default: n++; break;
            }
        }
        return (d, r, n);
    }
}
