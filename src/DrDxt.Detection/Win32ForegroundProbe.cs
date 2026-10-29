using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Detection;

/// <summary>
/// Qui est au premier plan, lu a la source : <c>GetForegroundWindow</c> puis le PID de sa fenetre. Deux API, aucune
/// elevation, aucun hook, aucune injection : utilisable dans un jeu protege. <c>null</c> sans fenetre au premier plan
/// (bureau verrouille, transition d'ecran, session distante qui bascule) ou PID nul : inconnu pour
/// <see cref="FocusObservation"/>, jamais une perte de focus.
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
