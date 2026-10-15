namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Suivi des nonces anti-rejeu.
//
// Sécurité : une requête valide (au HMAC correct) ne doit pas pouvoir être rejouée. Chaque
// requête porte un nonce unique ; le serveur enregistre les nonces vus et rejette toute
// répétition (fail-closed). Thread-safe, pour qu'une future boucle d'acceptation concurrente
// soit sûre. Il garde un ensemble en mémoire sans borne (les sessions sont courtes) ; une version
// ultérieure pourra le borner ou le fenêtrer dans le temps quand le helper tournera longtemps.
// -----------------------------------------------------------------------
public sealed class NonceTracker
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Enregistre le nonce de façon atomique. Rend true s'il était neuf (accepter),
    /// false s'il était déjà vu (rejeu → rejeter). Un nonce vide ou blanc est traité
    /// comme un rejeu (jamais neuf) pour qu'un nonce absent ne passe jamais la
    /// vérification anti-rejeu.
    /// </summary>
    public bool TryRegister(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return false;

        lock (_gate)
        {
            return _seen.Add(nonce);
        }
    }
}
