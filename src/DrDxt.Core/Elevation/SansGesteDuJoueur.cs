namespace DrDxt.Core;

/// <summary>
/// Un chemin que le joueur n'a pas déclenché : au lancement (session interrompue, protection rallumée, preuve du repos de la
/// carte), en veille (le ping pendant la partie), entre deux parties (essai en alternance, retrait de ce qui coûte, garde
/// d'incident, surveillance de la carte). Dans cette portée, le client qui lance le helper ne le lance pas : une invite
/// Windows qui s'ouvre seule, en pleine partie ou au démarrage, est refusée par réflexe, et le joueur ne sait pas d'où elle
/// vient. Le helper déjà ouvert répond ; sinon l'action dit qu'elle n'a pas pu, et attend un clic.
///
/// La portée suit le fil asynchrone (await et Task.Run compris) et seulement lui : un clic du joueur, sur un autre fil,
/// garde le droit de lancer le helper.
///
/// Limite : une boucle de fond NÉE dans la portée (le PeriodicTimer du plafond d'envoi allumé par la veille du ping, par
/// exemple) reste sans geste toute sa vie. Ne jamais y mettre en file une action de clic : elle ne pourrait plus lancer
/// le helper.
/// </summary>
public static class SansGesteDuJoueur
{
    private static readonly AsyncLocal<bool> Portee = new();

    public static bool Actif => Portee.Value;

    /// <summary>Ouvre la portée jusqu'au Dispose. À poser dans la méthode même qui attend le chemin sans geste.</summary>
    public static IDisposable Ouvrir()
    {
        var avant = Portee.Value;
        Portee.Value = true;
        return new Fermeture(avant);
    }

    private sealed class Fermeture(bool avant) : IDisposable
    {
        public void Dispose() => Portee.Value = avant;
    }
}
