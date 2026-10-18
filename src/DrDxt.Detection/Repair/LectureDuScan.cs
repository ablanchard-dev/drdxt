namespace DrDxt.Detection.Repair;

/// <summary>
/// Une source datée (un journal de Windows) lue une fois pour tout un scan, depuis <paramref name="lireDepuis"/> : chaque
/// famille qui la demande sur une fenêtre plus courte en reçoit les éléments datés de sa fenêtre. Une fenêtre plus
/// large que celle lue relit la source. Une source qui ne se lit pas (null) le reste pour tout le scan.
/// </summary>
internal sealed class LectureDuScan<T>(Func<DateTimeOffset, IReadOnlyList<T>?> lire, Func<T, DateTimeOffset> quand, DateTimeOffset lireDepuis)
{
    private readonly Lazy<IReadOnlyList<T>?> _lu = new(() => lire(lireDepuis));

    public IReadOnlyList<T>? Depuis(DateTimeOffset depuis)
        => depuis < lireDepuis ? lire(depuis) : _lu.Value?.Where(x => quand(x) >= depuis).ToList();
}
