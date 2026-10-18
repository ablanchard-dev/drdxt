namespace DrDxt.Core;

/// <summary>
/// La portée d'une pose de levier dont l'origine n'est pas encore notée. Ouverte par le chemin de pose autour du seul
/// ApplyAsync, dans la même méthode ; le client élevé de l'App marque alors les écritures journalisées, et la fermeture,
/// juste après l'enregistrement de l'origine, les acquitte. Sans portée (oubli, tâche de fond, retour arrière), tout part
/// nu : le comportement d'avant le journal, jamais un rejeu à tort.
/// Le chemin la tient par un <c>using</c> : une pose qui échoue, ou une origine qui n'a pas pu être notée, la laisse sans
/// acquit. Les entrées restent alors au journal du helper, qui rendra l'origine si l'App meurt sans l'avoir notée.
/// </summary>
public sealed class PorteeDePose : IDisposable
{
    private static readonly AsyncLocal<PorteeDePose?> EnCours = new();

    private readonly List<Func<Task<Result>>> _acquits = [];
    private readonly object _verrou = new();
    private readonly PorteeDePose? _avant;
    private bool _fermee;

    private PorteeDePose(PorteeDePose? avant) => _avant = avant;

    /// <summary>La portée ouverte dans ce flux, ou null. Une portée fermée n'est plus ouverte, même là où le flux la voit
    /// encore (une fermeture dans une méthode asynchrone ne remonte pas à l'appelant).</summary>
    public static PorteeDePose? Courante => EnCours.Value is { } p && !p.EstFermee ? p : null;

    private bool EstFermee { get { lock (_verrou) return _fermee; } }

    public static PorteeDePose Ouvrir()
    {
        var p = new PorteeDePose(EnCours.Value);
        EnCours.Value = p;
        return p;
    }

    /// <summary>Ferme sans acquitter (déjà fermée : rien), et rend au flux la portée d'avant.</summary>
    public void Dispose()
    {
        lock (_verrou) _fermee = true;
        if (ReferenceEquals(EnCours.Value, this)) EnCours.Value = _avant;
    }

    /// <summary>Le temps du bloc, ce flux n'a plus de portée : un retour arrière en pleine pose part nu.</summary>
    public static IDisposable Suspendre()
    {
        var avant = EnCours.Value;
        EnCours.Value = null;
        return new Retablir(avant);
    }

    /// <summary>Le client qui a marqué une pose note comment l'acquitter.</summary>
    public void Noter(Func<Task<Result>> acquitter)
    {
        lock (_verrou) _acquits.Add(acquitter);
    }

    /// <summary>Ferme la portée et acquitte chaque pose marquée, dans l'ordre. Un acquit refusé est rendu : la pose reste,
    /// son origine est déjà notée chez l'App.</summary>
    public async Task<Result> FermerAsync()
    {
        List<Func<Task<Result>>> acquits;
        lock (_verrou)
        {
            if (_fermee) return Result.Ok();
            _fermee = true;
            acquits = [.. _acquits];
        }
        Result premier = Result.Ok();
        foreach (var acquitter in acquits)
        {
            Result r;
            try { r = await acquitter().ConfigureAwait(false); }
            catch (Exception ex) { r = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message); }
            if (!r.IsOk && premier.IsOk) premier = r;
        }
        return premier;
    }

    private sealed class Retablir(PorteeDePose? avant) : IDisposable
    {
        public void Dispose() => EnCours.Value = avant;
    }
}
