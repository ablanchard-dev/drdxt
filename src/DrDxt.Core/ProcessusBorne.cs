using System.Diagnostics;
using System.Text;

namespace DrDxt.Core;

/// <summary>Le seul lanceur des outils de Windows dont DrDXT lit la sortie (dism, bcdedit, pnputil, shutdown, sc, powershell,
/// vssadmin, logman). Le chemin est absolu : un nom nu partirait dans l'ordre de recherche de CreateProcess avant System32.
/// Sans dossier de travail donné, l'outil tourne dans System32, jamais dans le dossier courant de l'appelant. Les deux flux sont
/// lus pendant l'attente (non lu, un flux plein bloque l'outil jusqu'à la borne ; lu avant l'attente, il la rend inopérante) et
/// gardés jusqu'à <c>maxCaracteres</c>, la suite lue et jetée. À la borne, l'arbre du processus est arrêté. L'outil sorti, la
/// fin des flux s'attend elle aussi, bornée : un petit-enfant qui en hérite les garde ouverts ; au-delà, l'arbre est arrêté et
/// ce qui a été lu est rendu, avec le vrai code de l'outil. L'environnement est celui de l'appelant, ou ce que
/// <paramref name="psi"/> y fixe. Ne lève que sur un chemin non absolu ou si le lancement lève.</summary>
public static class ProcessusBorne
{
    /// <summary>La fin des flux d'un outil sorti s'attend au moins ce temps, même quand la borne est presque épuisée.</summary>
    private static readonly TimeSpan PlancherDesFlux = TimeSpan.FromSeconds(2);

    /// <param name="Lance">Le processus a démarré.</param>
    /// <param name="Fini">Il a rendu la main avant la borne ; sinon il a été arrêté (au mieux), et Code, Sortie, Erreurs sont
    /// vides.</param>
    public readonly record struct Issue(bool Lance, bool Fini, int Code, string Sortie, string Erreurs, int Pid);

    public static Issue Executer(ProcessStartInfo psi, TimeSpan borne, int maxCaracteres = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(psi);
        if (!Path.IsPathFullyQualified(psi.FileName))
            throw new ArgumentException($"Not an absolute path: {psi.FileName}", nameof(psi));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (string.IsNullOrEmpty(psi.WorkingDirectory))
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var chrono = Stopwatch.StartNew();
        using var p = Process.Start(psi);
        if (p is null) return new(false, false, -1, "", "", 0);
        var gardeSortie = new StringBuilder();
        var gardeErreurs = new StringBuilder();
        var sortie = Lire(p.StandardOutput, gardeSortie, maxCaracteres);
        var erreurs = Lire(p.StandardError, gardeErreurs, maxCaracteres);
        if (!p.WaitForExit(borne))
        {
            Arreter(p);
            return new(true, false, -1, "", "", p.Id);
        }
        var reste = borne - chrono.Elapsed;
        if (!Task.WaitAll([sortie, erreurs], reste > PlancherDesFlux ? reste : PlancherDesFlux))
            Arreter(p);
        return new(true, true, p.ExitCode, Copie(gardeSortie), Copie(gardeErreurs), p.Id);
    }

    // Au mieux : un processus protégé, ou déjà en train de sortir, refuse l'arrêt ; la borne est dépassée de toute façon.
    private static void Arreter(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException
                                      or NotSupportedException) { }
    }

    private static string Copie(StringBuilder garde)
    {
        lock (garde) return garde.ToString();
    }

    private static async Task Lire(StreamReader flux, StringBuilder garde, int max)
    {
        var tampon = new char[4096];
        int n;
        try
        {
            while ((n = await flux.ReadAsync(tampon).ConfigureAwait(false)) > 0)
                lock (garde)
                    if (garde.Length < max) garde.Append(tampon, 0, Math.Min(n, max - garde.Length));
        }
        catch (Exception e) when (e is ObjectDisposedException or IOException or InvalidOperationException)
        {
            // Le processus est libéré pendant qu'un petit-enfant tenait encore le flux : ce qui a été lu reste gardé.
        }
    }
}
