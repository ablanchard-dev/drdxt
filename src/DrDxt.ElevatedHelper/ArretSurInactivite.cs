namespace DrDxt.ElevatedHelper;

/// <summary>
/// Un helper élevé lancé à la demande ne vit plus pour toujours. Après <c>delai</c> sans aucune
/// connexion, et sans bail actif, il s'arrête : un processus administrateur oublié n'est plus là pour personne. L'App ne le
/// relance que sur un geste du joueur (les tâches de fond passent par un client qui ne lance jamais le helper).
/// </summary>
public sealed class ArretSurInactivite(TimeSpan delai, Func<DateTimeOffset> maintenant)
{
    private long _derniere = maintenant().UtcTicks;

    private int _dernierClient;

    public void Activite() => Interlocked.Exchange(ref _derniere, maintenant().UtcTicks);

    /// <summary>Le processus du dernier client authentifié (l'App). Inconnu = 0.</summary>
    public void ClientAuthentifie(int? pid)
    {
        Activite();
        if (pid is > 0) Interlocked.Exchange(ref _dernierClient, pid.Value);
    }

    /// <param name="vivant">Le processus existe-t-il encore ? Le client parti et aucun bail : arrêt sans attendre le délai
    /// (« à la fermeture de l'App »). Null : seul le délai compte.</param>
    public bool DoitSArreter(int bauxActifs, Func<int, bool>? vivant = null)
    {
        if (bauxActifs != 0) return false;
        var client = Volatile.Read(ref _dernierClient);
        if (client > 0 && vivant is not null && !vivant(client)) return true;
        return maintenant().UtcTicks - Interlocked.Read(ref _derniere) >= delai.Ticks;
    }
}
