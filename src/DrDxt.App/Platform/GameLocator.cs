using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Détection du jeu par sa fenêtre, sans viser une liste de tous les titres. Un « jeu » est la fenêtre
// de premier niveau visible, de la taille de l'écran (plein écran ou sans bordure), qui n'est ni le
// shell, ni le bureau, ni DrDXT, ni un programme qu'on sait ne pas être un jeu (NomExeDeJeu : navigateurs,
// Discord, lanceurs), ni une appli agrandie. Une liste courte des jeux les plus joués passe avant
// (NomExeDeJeu.EstUnJeuConnu) : ceux-là sont reconnus même en fenêtré ou réduits. On mesure ce processus avec PresentMon : ça marche pour Warzone, Valorant,
// CS2, Apex, Fortnite, n'importe quel titre, sans entretien, et ça reste honnête (on mesure le
// vrai jeu ; un benchmark synthétique ne le représenterait pas et la doctrine l'interdit). Pas
// de jeu trouvé → null → le scan reste honnêtement en mode config seule, jamais un chiffre inventé.
//
// La détection suit l'heuristique classique du plein écran sous Windows (Microsoft Learn
// GetForegroundWindow + GetWindowRect comparé aux bornes de l'écran ; Richard Banks 2007) : une
// fenêtre dont le rectangle couvre tout un écran et qui n'est pas le shell est une appli plein écran.
// -----------------------------------------------------------------------
public interface IGameLocator
{
    /// <summary>Le PID du jeu lancé (plein écran ou sans bordure), ou null s'il n'y en a pas.</summary>
    int? LocateRunningGamePid();

    /// <summary>
    /// Un jeu est-il ouvert, même réduit ? Pour les gardes qui refusent de mesurer sous charge, pas pour
    /// la capture : un jeu réduit n'affiche aucune image à mesurer.
    ///
    /// Mesuré sur le poste : une fenêtre plein écran réduite reste « visible », mais sa
    /// position tombe à (0,1004)-(160,1032) et <see cref="LocateRunningGamePid"/> ne la voit plus, alors
    /// que le jeu tourne toujours (Alt+Tab sur un plein écran exclusif le réduit).
    /// </summary>
    bool JeuOuvert() => LocateRunningGamePid() is not null;

    /// <summary>
    /// La fenêtre de CE jeu couvre-t-elle encore un écran, quelle que soit la fenêtre au premier plan ?
    /// Sur deux écrans, cliquer un stream en plein écran pendant la capture le fait passer
    /// au premier plan ; comparer au seul jeu localisé aurait écarté une capture valide.
    /// </summary>
    bool EstAffiche(int pid) => LocateRunningGamePid() == pid;

    /// <summary>
    /// Comment s'appelle ce jeu : la seule chose qui rend deux mesures comparables dans le temps.
    ///
    /// Le journal de performance comparait deux mesures sur la seule foi de l'identifiant
    /// machine. Or le produit mesure le jeu qui tourne, quel qu'il soit : scanner pendant Warzone
    /// puis pendant un autre titre remplissait le journal de deux echelles de FPS sans rapport, et
    /// la comparaison annonçait une « degradation » qui n'etait qu'un changement de jeu.
    ///
    /// Le nom du processus suffit : il ne quitte pas la machine, il n'identifie personne, et il est
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
/// Le chemin de l'executable du jeu en cours, pour lire les reglages du pilote au
/// profil que le pilote applique a ce jeu.
/// Droit demande : <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, le plus faible qui rende le chemin
/// (celui du Gestionnaire des taches). Aucune lecture de memoire, aucune injection. Refus (processus
/// protege, anti-triche) ⇒ null ⇒ lecture au profil global, etiquetee comme telle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunningGameExecutable(IGameLocator locator) : DrDxt.Application.Tweaks.Hardware.IGameExecutable
{
    public string? RunningGameExecutablePath()
    {
        try
        {
            return locator.LocateRunningGamePid() is { } pid && pid > 0 ? Win32GameLocator.CheminDe((uint)pid) : null;
        }
        catch
        {
            return null;
        }
    }

}

/// <summary>Valeur par défaut qui échoue proprement : ne trouve jamais de jeu (hors Windows,
/// sans écran, tests). Le scan porte honnêtement sur la config seule.</summary>
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

            // 1) D'abord la fenêtre au premier plan : un jeu connu (fenêtré compris), ou un plein écran auquel le joueur joue.
            //    Un jeu connu laissé ouvert sur l'autre écran ne passe pas devant le jeu joué.
            var premierPlan = GetForegroundWindow();
            GetWindowThreadProcessId(premierPlan, out var pidPremierPlan);
            if (pidPremierPlan != 0 && pidPremierPlan != self && EstUnJeuConnu(pidPremierPlan))
                return (int)pidPremierPlan;
            if (IsFullscreenGameWindow(premierPlan, self, shell, desktop, out var fgPid))
                return (int)fgPid;

            // 2) Un jeu connu affiché ailleurs (fenêtré, DrDXT au premier plan) : il ne se rate pas.
            if (JeuConnuAffiche(self) is { } connu)
                return (int)connu;

            // 3) Sinon, chercher une fenêtre visible de la taille de l'écran : un jeu sans bordure garde
            //    sa taille même quand DrDXT est au-dessus (le cas courant en compétitif).
            uint found = 0;
            EnumWindows((h, _) =>
            {
                if (IsFullscreenGameWindow(h, self, shell, desktop, out var pid))
                {
                    found = pid;
                    return false; // on s'arrête à la première correspondance
                }
                return true;
            }, IntPtr.Zero);
            return found != 0 ? (int)found : null;
        }
        catch
        {
            return null; // jamais d'exception : pas de jeu trouvé → scan dégradé honnête
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
        if (LocateRunningGamePid() is not null || JeuConnuLance()) return true;
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
        if (PasUnJeu(pid)) return false;
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

    // Mesuré sur le poste en usage normal : le localisateur désignait explorer comme le jeu, par
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

        // TextInputHost garde une CoreWindow
        // « visible » de la taille de l'ecran mais masquee par DWM, et passait pour le jeu. Une fenetre
        // masquee n'est pas affichee (Microsoft Learn, DWMWINDOWATTRIBUTE, DWMWA_CLOAKED).
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // La fenetre visible de NVIDIA Overlay est
        // WS_EX_NOACTIVATE : elle ne peut jamais passer au premier plan, donc ce n'est pas le jeu
        // auquel on joue. Regle generique, pas une liste de noms : vaut pour tout overlay.
        if ((GetWindowLong(hwnd, GwlExStyle) & WsExNoActivate) != 0)
            return false;

        if (EstUneAppliAgrandie(GetWindowLong(hwnd, GwlStyle)))
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

        // Couvre (≈) tout l'écran → jeu en plein écran ou sans bordure, sauf un programme qu'on sait ne pas être un jeu
        // (navigateur, Discord, lanceur), lu après la géométrie pour n'ouvrir que les processus plein écran.
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom
            && !PasUnJeu(pid);
    }

    private static bool PasUnJeu(uint pid)
    {
        var chemin = CheminDe(pid);
        return DrDxt.Core.NomExeDeJeu.EstSurementPasUnJeu(chemin is null ? null : System.IO.Path.GetFileName(chemin), chemin);
    }

    /// <summary>Le premier jeu connu dont une fenêtre est affichée (visible, non réduite, non masquée), ou null.</summary>
    private static uint? JeuConnuAffiche(uint self)
    {
        uint trouve = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0 || pid == self || !EstUnJeuConnu(pid))
                return true;
            trouve = pid;
            return false;
        }, IntPtr.Zero);
        return trouve != 0 ? trouve : null;
    }

    /// <summary>Un jeu connu lancé, même réduit ou derrière une autre fenêtre.</summary>
    private static bool JeuConnuLance()
    {
        foreach (var p in System.Diagnostics.Process.GetProcesses())
            using (p)
                if (DrDxt.Core.NomExeDeJeu.EstUnJeuConnu(p.ProcessName + ".exe")) return true;
        return false;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static bool EstUnJeuConnu(uint pid) => DrDxt.Core.NomExeDeJeu.EstUnJeuConnu(CheminDe(pid) is { } c ? System.IO.Path.GetFileName(c) : null);

    // Relu à chaque appel, sans cache : Windows réutilise vite un PID, un cache par PID donnerait au nouveau
    // processus le chemin de l'ancien. La lecture aux droits limités coûte quelques microsecondes.
    /// <summary>Le chemin de l'exécutable, avec le droit le plus faible qui le rende ; null si refusé (anti-triche).</summary>
    internal static string? CheminDe(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var taille = (uint)buffer.Capacity;
            return QueryFullProcessImageName(h, 0, buffer, ref taille) ? buffer.ToString(0, (int)taille) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaCloaked = 14; // DWMWINDOWATTRIBUTE.DWMWA_CLOAKED (dwmapi.h)

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    private const int GwlExStyle = -20;           // GWL_EXSTYLE (winuser.h)
    private const int GwlStyle = -16;             // GWL_STYLE (winuser.h)
    private const int WsMaximize = 0x01000000;    // WS_MAXIMIZE (winuser.h)
    private const int WsMaximizeBox = 0x00010000; // WS_MAXIMIZEBOX (winuser.h)

    /// <summary>Une appli agrandie sur un écran sans barre des tâches (Discord sur le second écran) en couvre toute la
    /// surface, comme un jeu sans bordure. Agrandie par Windows et munie du bouton « Agrandir » : c'est une appli de bureau.
    /// Un jeu plein écran ou sans bordure est une popup sans ce bouton. Règle générique, pas une liste de noms ; un jeu
    /// qui garderait ce bouton en plein écran ne serait plus vu.</summary>
    internal static bool EstUneAppliAgrandie(int style) => (style & WsMaximize) != 0 && (style & WsMaximizeBox) != 0;
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
