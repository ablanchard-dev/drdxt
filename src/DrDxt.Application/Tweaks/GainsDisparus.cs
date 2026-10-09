using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>Un levier applique dont la valeur est retombee sur celle d'avant.</summary>
/// <param name="TweakId">Le levier concerne.</param>
/// <param name="Cible">La feuille relue, pour que le constat soit verifiable a la main.</param>
public sealed record GainDisparu(string TweakId, string? Cible);

/// <summary>
/// Remarquer qu'un gain a disparu — sans jamais le reecrire.
///
/// ══ ce que ce type n'est pas, et pourquoi ══
/// Le carnet de la boucle 17 demandait d'abord « ajouter les onze leviers manquants au
/// catalogue de reconciliation ». **C'etait faux, et ca aurait ete nuisible.** Le catalogue
/// (`DefaultReconcilableTweakCatalog`) explique deja ses absences, et ses raisons sont bonnes :
///
///   • les leviers HKCU (Game DVR, Game Mode, VRR) sont ABSENTS parce que le reconciliateur de
///     boot tourne en LocalSystem — `Registry.CurrentUser` y designe la ruche de SYSTEM, donc une
///     reapplication toucherait la MAUVAISE ruche. Et ils persistent nativement au reboot ;
///   • `latency.renderQueueCap` et `vrrVsyncCap` sont absents parce que le reconciliateur est
///     VOLONTAIREMENT incapable d'ecrire le GPU (`DisabledNvApiActions`) : les sonder produirait
///     une derive qu'il n'a pas le droit de corriger.
///
/// La question n'a jamais ete « comment les relire » — c'est fait — mais **« qui a le droit de les
/// reecrire »**, et la reponse ne doit pas etre le service SYSTEM.
///
/// ══ ce qu'il est ══
/// Le manque reel n'est donc pas la reapplication : c'est que **personne ne REMARQUE** quand un
/// gain a disparu. Ce type lit, compare, et se tait sur le reste. Aucune ecriture, aucune ruche
/// choisie a sa place : la feuille vient du snapshot que le levier a lui-meme produit.
///
/// Et il ne rend que les gains disparus. Un levier `DifferentDeAvant` n'est pas rendu comme
/// « applique » : la relecture ne sait pas le prouver, et le silence est plus honnete qu'une
/// affirmation.
/// </summary>
public static class GainsDisparus
{
    /// <summary>Parmi les leviers appliques, ceux dont la valeur est revenue a l'etat d'avant.</summary>
    /// <param name="appliques">Les snapshots du manifeste : ce que le produit croit avoir applique.</param>
    /// <param name="lecteur">Lecture registre non elevee — lire ne demande pas l'UAC.</param>
    public static IReadOnlyList<GainDisparu> Detecter(
        IEnumerable<TweakSnapshot>? appliques, IRegistryReader lecteur)
    {
        if (appliques is null) return [];

        var perdus = new List<GainDisparu>();
        foreach (var snapshot in appliques)
        {
            // Vu sur capture (08/10/2026) : une origine égale à la valeur posée ne porte aucun changement ; la relire
            // « revenue à l'avant » criait au gain perdu sur un réglage en place.
            if (snapshot is null || string.Equals(snapshot.AppliedValueJson, snapshot.OriginalValueJson, StringComparison.Ordinal)) continue;
            var relu = TweakReadback.Relire(snapshot, lecteur);

            // Seul `RevenuAvantApplication` compte. `NonLu` et `NonRelisable` sont des trous, et
            // les compter comme des pertes ferait crier le produit sur ce qu'il n'a pas su lire —
            // la faute exacte que ce depot corrige partout ailleurs.
            if (relu.Etat == EtatRelu.RevenuAvantApplication)
                perdus.Add(new GainDisparu(snapshot.TweakId, relu.Cible));
        }

        return perdus;
    }
}
