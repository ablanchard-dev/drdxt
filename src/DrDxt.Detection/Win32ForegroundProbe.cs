using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Detection;

/// <summary>
/// Qui est au premier plan, lu a la source : <c>GetForegroundWindow</c> puis le PID de sa fenetre.
///
/// Deux API, aucune elevation, aucun hook, aucune injection — c'est une lecture d'etat que Windows
/// expose a tout le monde. C'est precisement ce qui la rend utilisable dans un jeu protege.
///
/// Rend <c>null</c> plutot que de deviner : pas de fenetre au premier plan (bureau verrouille,
/// transition d'ecran, session distante en train de basculer) ou PID nul. Le contrat de
/// <see cref="FocusObservation"/> compte ces lectures comme INCONNUES, ni pour ni contre — les
/// traiter comme une perte de focus invaliderait des passes parfaitement bonnes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ForegroundProbe : IForegroundProbe
{
    public int? ForegroundProcessId()
    {
        try
        {
            var fenetre = GetForegroundWindow();
            if (fenetre == IntPtr.Zero) return null;

            _ = GetWindowThreadProcessId(fenetre, out var pid);
            return pid == 0 ? null : (int)pid;
        }
        catch
        {
            return null;   // une lecture qui echoue est une inconnue, jamais une absence.
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
