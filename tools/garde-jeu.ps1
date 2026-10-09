<#
.SYNOPSIS
  Refuse de laisser compiler ou tester pendant qu'un jeu tourne.

.DESCRIPTION
  Le PC de reference est a la fois le banc de dev et une machine de jeu. Mesure du 11/09/2026 au soir :
  pendant qu'il jouait, nos compilations et nos suites de tests lui ont coute ~16-20 % de ses
  images par seconde (Warzone est limite par le PROCESSEUR sur un i7-14700F : chaque coeur pris
  par le compilateur est pris au jeu).

  Ce script REFUSE (code de sortie 2) au lieu de laisser la suite continuer.

.HOW
  A appeler AVANT chaque dotnet build / dotnet test, dans la MEME commande, pour que le refus
  arrete vraiment la suite :

      powershell -NoProfile -File tools\garde-jeu.ps1; if ($LASTEXITCODE -eq 0) { dotnet build DrDXT.sln }
      powershell -NoProfile -File tools\garde-jeu.ps1; if ($LASTEXITCODE -eq 0) { dotnet test DrDXT.sln }

.NOTES
  DEUX detections, dans cet ordre :

  1. Par NOM de processus (-NomsJeux, defaut « cod »). Liste OUVERTE, volontairement courte.
  2. Par FENETRE PLEIN ECRAN : c'est la regle du depot, pas une deuxieme liste. Le produit ne
     tient AUCUNE liste de jeux (decision produit, cf. l'en-tete de
     src/DrDxt.App/Platform/GameLocator.cs) : « un jeu » y est la fenetre visible de premier
     niveau qui couvre un ecran entier et qui n'est ni le shell, ni le bureau, ni DrDXT.
     La MEME heuristique est refaite ici en PowerShell, en LECTURE SEULE, sans charger la
     moindre DLL de DrDXT : la garde doit pouvoir dire non AVANT que le depot compile, donc
     elle ne peut pas dependre de ce qu'elle protege.

  Sorties : 0 = aucun jeu vu, on peut compiler. 2 = jeu vu, REFUS. 3 = la garde elle-meme a
  echoue (dans le doute on refuse aussi : une garde muette est pire qu'une garde absente).
#>
[CmdletBinding()]
param(
    # Liste OUVERTE, par nom de processus (sans .exe). Completer au fil des titres joues.
    [string[]] $NomsJeux = @('cod'),

    # Processus ignores par la detection plein ecran : DrDXT lui-meme, le shell, et les overlays
    # connus qui posent une fenetre invisible de la taille de l'ecran sans etre un jeu. Mesure du
    # 12/09/2026 : « NVIDIA Overlay » (NVIDIA App) a fait refuser la garde alors qu'aucun jeu ne
    # tournait. Le VRAI GameLocator (src/DrDxt.App/Platform/GameLocator.cs) a la MEME lacune
    # (aucun filtre d'overlay) : note pour une correction du produit, pas seulement de la garde.
    [string[]] $Ignorer = @('DrDxt.App', 'DrDxt.ElevatedHelper', 'explorer', 'Idle', 'NVIDIA Overlay'),

    # N'utilise QUE la detection par nom (pour se tester sans dependre d'un ecran).
    [switch] $SansPleinEcran
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Refuser([string] $raison) {
    Write-Host "REFUS : $raison" -ForegroundColor Red
    Write-Host "Aucune compilation ni suite de tests pendant qu'un jeu tourne (feedback_pas_de_charge_pendant_jeu)."
    exit 2
}

try {
    # ---- 1. par nom -----------------------------------------------------------------
    foreach ($nom in $NomsJeux) {
        $p = Get-Process -Name $nom -ErrorAction SilentlyContinue
        if ($p) {
            $pids = ($p | ForEach-Object { $_.Id }) -join ', '
            Refuser "le processus '$nom' tourne (PID $pids)."
        }
    }

    if ($SansPleinEcran) {
        Write-Host "OK : aucun processus de jeu nomme ne tourne (detection plein ecran desactivee)."
        exit 0
    }

    # ---- 2. par fenetre plein ecran -------------------------------------------------
    if (-not ('GardeJeuWin32' -as [type])) {
        Add-Type -Namespace '' -Name 'GardeJeuWin32' -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
'@
    }

    $shell = [GardeJeuWin32]::GetShellWindow()
    $bureau = [GardeJeuWin32]::GetDesktopWindow()
    $moi = $PID
    $trouve = $null

    $rappel = [GardeJeuWin32+EnumWindowsProc] {
        param([IntPtr] $hwnd, [IntPtr] $lParam)
        if ($hwnd -eq $shell -or $hwnd -eq $bureau) { return $true }
        if (-not [GardeJeuWin32]::IsWindowVisible($hwnd)) { return $true }

        # DWMWA_CLOAKED (14) : une fenetre UWP « masquee » (TextInputHost, ShellExperienceHost…)
        # reste IsWindowVisible et fait la taille de l'ecran. Mesure du 11/09/2026 : sans ce
        # filtre la garde refusait de compiler sur un bureau vide. Le produit, lui, ne voit pas
        # ce faux positif : il ne cherche un jeu que pendant un scan, au premier plan.
        $masque = 0
        if ([GardeJeuWin32]::DwmGetWindowAttribute($hwnd, 14, [ref] $masque, 4) -eq 0 -and $masque -ne 0) { return $true }

        # WS_EX_NOACTIVATE (0x08000000, GWL_EXSTYLE = -20) : jamais au premier plan, donc pas un jeu.
        # MEME regle que GameLocator (plan M5, 14/09/2026) : c'est elle qui ecarte NVIDIA Overlay.
        if (([GardeJeuWin32]::GetWindowLong($hwnd, -20) -band 0x08000000) -ne 0) { return $true }

        $procId = 0
        [void][GardeJeuWin32]::GetWindowThreadProcessId($hwnd, [ref] $procId)
        if ($procId -eq 0 -or $procId -eq $moi) { return $true }

        $r = New-Object GardeJeuWin32+RECT
        if (-not [GardeJeuWin32]::GetWindowRect($hwnd, [ref] $r)) { return $true }

        $mi = New-Object GardeJeuWin32+MONITORINFO
        $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
        $mon = [GardeJeuWin32]::MonitorFromWindow($hwnd, 2)
        if (-not [GardeJeuWin32]::GetMonitorInfo($mon, [ref] $mi)) { return $true }

        # Couvre l'ecran entier => plein ecran ou sans bordure.
        if ($r.Left -le $mi.rcMonitor.Left -and $r.Top -le $mi.rcMonitor.Top -and
            $r.Right -ge $mi.rcMonitor.Right -and $r.Bottom -ge $mi.rcMonitor.Bottom) {
            try { $nom = (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { return $true }
            if ($Ignorer -contains $nom) { return $true }
            $script:trouve = "$nom (PID $procId)"
            return $false
        }
        return $true
    }

    [void][GardeJeuWin32]::EnumWindows($rappel, [IntPtr]::Zero)

    if ($trouve) {
        Refuser "une fenetre plein ecran appartient a $trouve."
    }

    Write-Host "OK : aucun jeu detecte (ni par nom, ni en plein ecran). Compilation autorisee."
    exit 0
}
catch {
    Write-Host "REFUS : la garde n'a pas pu conclure ($($_.Exception.Message))." -ForegroundColor Red
    exit 3
}
