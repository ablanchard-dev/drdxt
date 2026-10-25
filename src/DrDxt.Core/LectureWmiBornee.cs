namespace DrDxt.Core;

/// <summary>
/// Toute lecture WMI de DrDXT passe ici. System.Management n'a de délai ni pour la connexion au scope ni pour l'exécution
/// (celui d'EnumerationOptions ne couvre que l'énumération) : sans borne, un winmgmt figé figerait l'appelant, le scan de
/// l'App, le commit du manifeste ou le tour du service, jusqu'au redémarrage. La lecture entière tourne sur un fil à part,
/// attendue au plus <see cref="Delai"/> ; au-delà, la valeur « figé » de l'appelant, qu'il dit comme une lecture ratée.
/// Un fil figé dans WMI ne se tue pas : il reste jusqu'au retour de winmgmt, un par lecture tant que winmgmt est figé,
/// borné par la cadence des appelants. Il ne tient rien de partagé : seul l'appelant tient ses verrous, et il repart.
/// Les écritures (activer la restauration du système) n'y passent pas : elles ne s'abandonnent pas en cours de route.
/// </summary>
public static class LectureWmiBornee
{
    /// <summary>Une lecture locale répond en millisecondes ; dix secondes ne coupent qu'un winmgmt qui ne répond plus.</summary>
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(10);

    public static T Lire<T>(Func<T> lire, Func<T> siFige, TimeSpan? delai = null)
    {
        // Un fil à lui, hors du pool : figé dans WMI, il ne prive pas les autres tâches d'un fil du pool.
        var lecture = Task.Factory.StartNew(lire, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return lecture.Wait(delai ?? Delai) ? lecture.Result : siFige();
    }

    /// <summary>Un compteur brut de Win32_PerfRawData. Absent, il lève au lieu de valoir 0 (ce que rend Convert sur null) :
    /// présent des deux côtés à 0, il donnait un delta nul, dit « 0 % » comme une lecture. L'appelant rend alors « non lu ».</summary>
    public static ulong Compteur(object? valeur)
        => valeur is null
            ? throw new InvalidDataException("WMI counter absent.")
            : Convert.ToUInt64(valeur, System.Globalization.CultureInfo.InvariantCulture);
}
