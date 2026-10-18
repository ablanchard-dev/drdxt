using System.Runtime.InteropServices;
using System.Threading;

namespace DrDxt.App;

/// <summary>
/// Une seule App par session du joueur : un second lancement réveille la fenêtre ouverte et se ferme. Deux App
/// feraient deux helpers et deux relevés qui s'écrivent dessus. Un mutex nommé dans l'espace de la session
/// (<c>Local\</c>, avec le SID du compte) dit si une App tourne ; un événement nommé lui demande de se montrer.
/// </summary>
public sealed class InstanceUnique : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _reveil;
    private readonly RegisteredWaitHandle _attente;

    private InstanceUnique(Mutex mutex, EventWaitHandle reveil, Action seMontrer)
    {
        _mutex = mutex;
        _reveil = reveil;
        _attente = ThreadPool.RegisterWaitForSingleObject(reveil, (_, _) => seMontrer(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static string NomDeSession()
        => $@"Local\DrDXT-App-{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}";

    /// <summary>La place de l'App, ou null quand une autre la tient déjà : celle-ci a alors été priée de se montrer.
    /// <paramref name="seMontrer"/> est appelé hors du fil d'interface à chaque lancement suivant.</summary>
    public static InstanceUnique? Prendre(string nom, Action seMontrer)
    {
        // Non possédé : son existence suffit (l'objet vit tant qu'une poignée est ouverte), et aucun fil n'a à le rendre.
        var mutex = new Mutex(initiallyOwned: false, nom, out var premiere);
        var reveil = new EventWaitHandle(false, EventResetMode.AutoReset, nom + "-Reveil");
        if (premiere) return new InstanceUnique(mutex, reveil, seMontrer);

        // Le lancement qui vient d'être fait a le premier plan : il le cède, sans quoi Windows refuserait à la première
        // App de passer devant (règles de SetForegroundWindow).
        AllowSetForegroundWindow(-1);
        reveil.Set();
        reveil.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _attente.Unregister(null);
        _reveil.Dispose();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
