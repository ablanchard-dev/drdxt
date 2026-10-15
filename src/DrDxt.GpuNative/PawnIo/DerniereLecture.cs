namespace DrDxt.GpuNative.PawnIo;

/// <summary>
/// La dernière lecture, resservie tant qu'elle a moins de <c>validite</c>. Une lecture coûteuse (les ventilateurs par la puce
/// Super I/O : ~79 ms de processeur) ne se refait pas chaque seconde. Une horloge qui recule relit.
/// </summary>
public sealed class DerniereLecture<T>(TimeSpan validite) where T : class
{
    private readonly object _verrou = new();
    private (T? Valeur, DateTimeOffset Quand)? _derniere;

    public T? Obtenir(Func<T?> lire, DateTimeOffset maintenant)
    {
        lock (_verrou)
        {
            if (_derniere is { } d && maintenant >= d.Quand && maintenant - d.Quand < validite) return d.Valeur;
            var valeur = lire();
            _derniere = (valeur, maintenant);
            return valeur;
        }
    }
}
