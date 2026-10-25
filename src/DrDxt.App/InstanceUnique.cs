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

    /// <summary>Une App tient-elle la place ? Sans la prendre : le relevé de fond se tait quand l'App montre déjà l'essai.</summary>
    public static bool Ouverte(string nom)
    {
        // Une App élevée crée un mutex qu'un relevé non élevé ne peut pas ouvrir : le refus dit qu'il existe.
        try
        {
            if (!Mutex.TryOpenExisting(nom, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static string NomDeSession()
        => $@"Local\DrDXT-App-{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}";

    /// <summary>La place de l'App, ou null quand une autre la tient déjà : celle-ci a alors été priée de se montrer.
    /// <paramref name="seMontrer"/> est appelé hors du fil d'interface à chaque lancement suivant.</summary>
    /// <param name="attente">Après une relance élevée, l'App qui s'éteint tient encore la place : la nouvelle la redemande
    /// jusqu'à ce délai, sans la réveiller, au lieu de se croire un second lancement et de s'arrêter.</param>
    public static InstanceUnique? Prendre(string nom, Action seMontrer, TimeSpan attente = default)
    {
        var limite = DateTime.UtcNow + attente;
        while (true)
        {
            var prise = PrendreUneFois(nom, seMontrer, essaiSuivant: DateTime.UtcNow < limite, out var reessayer);
            if (prise is not null || !reessayer) return prise;
            Thread.Sleep(100);
        }
    }

    private static InstanceUnique? PrendreUneFois(string nom, Action seMontrer, bool essaiSuivant, out bool reessayer)
    {
        reessayer = false;
        // Non possédé : son existence suffit (l'objet vit tant qu'une poignée est ouverte), et aucun fil n'a à le rendre.
        Mutex mutex;
        bool premiere;
        try { mutex = new Mutex(initiallyOwned: false, nom, out premiere); }
        catch (UnauthorizedAccessException)
        {
            // L'App élevée tient un mutex que ce lancement non élevé ne peut pas ouvrir : la place est prise. Elle est priée de
            // se montrer si son signal s'ouvre, et ce lancement se retire au lieu de rester sans fenêtre.
            try { if (EventWaitHandle.TryOpenExisting(nom + "-Reveil", out var signal)) using (signal) signal.Set(); }
            catch (UnauthorizedAccessException) { /* son signal non plus ne s'ouvre pas : rien d'autre à faire */ }
            return null;
        }
        var reveil = new EventWaitHandle(false, EventResetMode.AutoReset, nom + "-Reveil");
        if (premiere) return new InstanceUnique(mutex, reveil, seMontrer);
        if (essaiSuivant)
        {
            reveil.Dispose();
            mutex.Dispose();
            reessayer = true;
            return null;
        }

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
