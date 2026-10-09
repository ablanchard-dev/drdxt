using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Generic game detection — NO per-game list (project rule: never maintain a list of every competitive
// title). A "game" is simply the visible, monitor-sized (fullscreen or borderless) top-level
// window that is not the shell / desktop / DrDXT itself. We measure that process with PresentMon,
// so it works for Warzone, Valorant, CS2, Apex, Fortnite — any title — with zero maintenance,
// and stays HONEST (we measure the real game; a synthetic benchmark would not represent it and
// the doctrine bans it). No game found → null → the scan stays in honest config-only mode, never
// a fabricated number.
//
// Detection is the canonical Windows fullscreen heuristic (Microsoft Learn GetForegroundWindow +
// GetWindowRect vs the monitor bounds; Richard Banks 2007): a window whose rect covers a whole
// monitor and which is not the shell is a fullscreen app.
// -----------------------------------------------------------------------
public interface IGameLocator
{
    /// <summary>The PID of the game currently running (fullscreen/borderless), or null when none.</summary>
    int? LocateRunningGamePid();

    /// <summary>
    /// Un jeu est-il ouvert, même réduit ? Pour les gardes qui refusent de mesurer sous charge, pas pour
    /// la capture : un jeu réduit n'affiche aucune image à mesurer.
    ///
    /// 26/09/2026, mesuré sur le poste : une fenêtre plein écran réduite reste « visible », mais sa
    /// position tombe à (0,1004)-(160,1032) et <see cref="LocateRunningGamePid"/> ne la voit plus, alors
    /// que le jeu tourne toujours (Alt+Tab sur un plein écran exclusif le réduit).
    /// </summary>
    bool JeuOuvert() => LocateRunningGamePid() is not null;

    /// <summary>
    /// La fenêtre de CE jeu couvre-t-elle encore un écran, quelle que soit la fenêtre au premier plan ?
    /// 26/09/2026 : sur deux écrans, cliquer un stream en plein écran pendant la capture le fait passer
    /// au premier plan ; comparer au seul jeu localisé aurait écarté une capture valide.
    /// </summary>
    bool EstAffiche(int pid) => LocateRunningGamePid() == pid;

    /// <summary>
    /// Comment s'appelle ce jeu — la seule chose qui rend deux mesures comparables dans le temps.
    ///
    /// Le journal de performance comparait deux mesures sur la seule foi de l'identifiant
    /// MACHINE. Or le produit mesure le jeu qui tourne, quel qu'il soit : scanner pendant Warzone
    /// puis pendant un autre titre remplissait le journal de deux echelles de FPS sans rapport, et
    /// la comparaison annonçait une « degradation » qui n'etait qu'un changement de jeu.
    ///
    /// Le nom du processus suffit — il ne quitte pas la machine, il n'identifie personne, et il est
    /// stable d'une session a l'autre. <c>null</c> quand le PID est inconnu ou deja mort : « pas
    /// identifie » n'est pas « le meme jeu », et le journal se taira.
    /// </summary>
    string? NameOfGame(int pid)
    {
        // PID 0 est le processus « Idle » sous Windows : il repond, et le nommer ferait passer une
        // absence de jeu pour un titre stable.
        if (pid <= 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var nom = p.ProcessName;
            return string.IsNullOrWhiteSpace(nom) ? null : nom;
        }
        catch
        {
            // PID inconnu, processus deja termine, acces refuse : on ne sait pas.
            return null;
        }
    }
}

/// <summary>
/// PHASE 18 F1 — le chemin de l'executable du jeu en cours, pour lire les reglages du pilote au
/// profil que le pilote applique a ce jeu.
/// Droit demande : <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, le plus faible qui rende le chemin
/// (celui du Gestionnaire des taches). Aucune lecture de memoire, aucune injection. Refus (processus
/// protege, anti-triche) ⇒ null ⇒ lecture au profil global, etiquetee comme telle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunningGameExecutable(IGameLocator locator) : DrDxt.Application.Tweaks.Hardware.IGameExecutable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public string? RunningGameExecutablePath()
    {
        try
        {
            if (locator.LocateRunningGamePid() is not { } pid || pid <= 0) return null;
            var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                var taille = (uint)buffer.Capacity;
                return QueryFullProcessImageName(h, 0, buffer, ref taille) ? buffer.ToString(0, (int)taille) : null;
            }
            finally { CloseHandle(h); }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Fail-clean default: never locates a game (non-Windows / headless / tests). The scan
/// runs config-only, honestly.</summary>
public sealed class NullGameLocator : IGameLocator
{
    public int? LocateRunningGamePid() => null;
}

[SupportedOSPlatform("windows")]
public sealed class Win32GameLocator : IGameLocator
{
    public int? LocateRunningGamePid()
    {
        try
        {
            var self = (uint)Environment.ProcessId;
            var shell = GetShellWindow();
            var desktop = GetDesktopWindow();

            // 1) Prefer the FOREGROUND window (exclusive-fullscreen game the user is playing now).
            if (IsFullscreenGameWindow(GetForegroundWindow(), self, shell, desktop, out var fgPid))
                return (int)fgPid;

            // 2) Otherwise enumerate for a monitor-sized visible window — a borderless game stays
            //    sized even when DrDXT is on top (the common competitive case).
            uint found = 0;
            EnumWindows((h, _) =>
            {
                if (IsFullscreenGameWindow(h, self, shell, desktop, out var pid))
                {
                    found = pid;
                    return false; // stop at the first match
                }
                return true;
            }, IntPtr.Zero);
            return found != 0 ? (int)found : null;
        }
        catch
        {
            return null; // never throw — no game located → honest degraded scan
        }
    }

    public bool EstAffiche(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var self = (uint)Environment.ProcessId;
            var shell = GetShellWindow();
            var desktop = GetDesktopWindow();
            var vu = false;
            EnumWindows((h, _) =>
            {
                vu = IsFullscreenGameWindow(h, self, shell, desktop, out var p) && p == (uint)pid;
                return !vu;
            }, IntPtr.Zero);
            return vu;
        }
        catch
        {
            return false;
        }
    }

    public bool JeuOuvert()
    {
        if (LocateRunningGamePid() is not null) return true;
        try
        {
            // Une fenêtre réduite garde la taille qu'elle retrouvera (rcNormalPosition) : si elle couvre
            // un écran entier, c'est un plein écran qu'on a réduit. Même règle que le jeu affiché, sans liste.
            var self = (uint)Environment.ProcessId;
            var trouve = false;
            EnumWindows((h, _) => !(trouve = EstPleinEcranReduit(h, self)), IntPtr.Zero);
            return trouve;
        }
        catch
        {
            return false;
        }
    }

    internal static bool EstPleinEcranReduit(IntPtr h, uint self)
    {
        if (!IsIconic(h) || !IsWindowVisible(h)) return false;
        // Mêmes exclusions que pour le jeu affiché : masquée par DWM, ou incapable de passer au premier plan.
        if (DwmGetWindowAttribute(h, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if ((GetWindowLong(h, GwlExStyle) & WsExNoActivate) != 0) return false;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == 0 || pid == self || pid == PidDuShell()) return false;
        var p = new WindowPlacement { length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(h, ref p)) return false;
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref p.rcNormalPosition, MonitorDefaultToNearest), ref mi)) return false;
        // La taille seulement : rcNormalPosition est en coordonnées de la zone de travail, décalée de
        // l'écran quand la barre des tâches est en haut ou à gauche.
        return p.rcNormalPosition.Right - p.rcNormalPosition.Left >= mi.rcMonitor.Right - mi.rcMonitor.Left
            && p.rcNormalPosition.Bottom - p.rcNormalPosition.Top >= mi.rcMonitor.Bottom - mi.rcMonitor.Top;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement { public int length, flags, showCmd; public int minX, minY, maxX, maxY; public Rect rcNormalPosition; }

    // 26/09/2026, mesuré sur le poste en usage normal : le localisateur désignait explorer comme le jeu, par
    // intermittence. Le shell possède, en plus du bureau (déjà exclu), des fenêtres passagères qui couvrent
    // l'écran (sélecteur Alt+Tab, menu Démarrer, notifications : hôte XAML d'explorer). Toute fenêtre du
    // processus du shell est écartée : règle générique, pas une liste de noms.
    private static uint PidDuShell()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(shell, out var pid);
        return pid;
    }

    private static bool IsFullscreenGameWindow(IntPtr hwnd, uint selfPid, IntPtr shell, IntPtr desktop, out uint pid)
    {
        pid = 0;
        if (hwnd == IntPtr.Zero || hwnd == shell || hwnd == desktop || !IsWindowVisible(hwnd))
            return false;

        // 14/09/2026 (mesure sur le poste, aucun jeu lance) : TextInputHost garde une CoreWindow
        // « visible » de la taille de l'ecran mais masquee par DWM, et passait pour le jeu. Une fenetre
        // masquee n'est pas affichee (Microsoft Learn, DWMWINDOWATTRIBUTE, DWMWA_CLOAKED).
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // 14/09/2026 (plan M5, mesure sur le poste) : la fenetre visible de NVIDIA Overlay est
        // WS_EX_NOACTIVATE — elle ne peut jamais passer au premier plan, donc ce n'est pas le jeu
        // auquel on joue. Regle generique, pas une liste de noms : vaut pour tout overlay.
        if ((GetWindowLong(hwnd, GwlExStyle) & WsExNoActivate) != 0)
            return false;

        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || pid == selfPid || pid == PidDuShell())
            return false;

        if (!GetWindowRect(hwnd, out var r))
            return false;

        var mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(mon, ref mi))
            return false;

        // Covers (≈) the whole monitor → fullscreen / borderless game.
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaCloaked = 14; // DWMWINDOWATTRIBUTE.DWMWA_CLOAKED (dwmapi.h)

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    private const int GwlExStyle = -20;           // GWL_EXSTYLE (winuser.h)
    private const int WsExNoActivate = 0x08000000; // WS_EX_NOACTIVATE (winuser.h)
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public Rect rcMonitor, rcWork; public uint dwFlags; }
}
