# =============================================================================
# MESURE EN JEU : un avant/apres a un endroit fixe, en une commande.
#
# Nee le 25/09/2026 des tests faits a la main pendant une partie (COD, machine de reference) :
# 131,8 / 133,0 / 134,5 FPS au meme endroit, processeur a 76-79 %, paquet a 66,5 W, coeurs P
# plats a 4 000 MHz. Ce script refait exactement ces relevés, sans les pieges rencontres ce jour-la
# (noms de compteurs traduits sur un Windows francais, virgules decoupees par PowerShell).
#
# UTILISATION (console ADMINISTRATEUR : la trace ETW l'exige), pendant la partie, immobile :
#   powershell -NoProfile -File tools\mesure-en-jeu.ps1 -Processus cod -Etiquette AVANT
#   ... changer UN reglage, revenir au meme endroit ...
#   powershell -NoProfile -File tools\mesure-en-jeu.ps1 -Processus cod -Etiquette APRES
#
# Chaque capture ajoute une ligne a %LOCALAPPDATA%\DrDXT\mesures-en-jeu.csv.
#
# CE QU'IL NE FAIT PAS : il ne change aucun reglage et ne conclut rien. Un gain ne compte que s'il
# depasse l'ecart entre les captures d'un meme cote.
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Processus,
    [string]$Etiquette = 'MESURE',
    [int]$Captures = 3,
    [int]$Secondes = 30
)

$ErrorActionPreference = 'Stop'
$sys32 = [Environment]::GetFolderPath('System')
$logman = Join-Path $sys32 'logman.exe'
$smi = Join-Path $sys32 'nvidia-smi.exe'
$csv = Join-Path $env:LOCALAPPDATA 'DrDXT\mesures-en-jeu.csv'

$jeu = Get-Process -Name $Processus -ErrorAction SilentlyContinue | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $jeu) { Write-Host "Aucun processus « $Processus » : lance le jeu d'abord." ; exit 2 }

$dossier = Join-Path $env:TEMP 'drdxt-mesure-en-jeu'
New-Item -ItemType Directory -Force $dossier | Out-Null
if (-not (Test-Path $csv)) {
    'date;etiquette;processus;fps;low1;p99_ms;images_33ms;cpu_util;cpu_mhz_p;paquet_w;gpu_util' | Out-File -Encoding utf8 $csv
}

$ajoutees = 0
for ($n = 1; $n -le $Captures; $n++) {
    $etl = Join-Path $dossier ("{0}-{1}-{2:HHmmss}.etl" -f $Etiquette, $n, (Get-Date))
    & $logman stop DrDxtPresents -ets 2>$null | Out-Null
    & $logman create trace DrDxtPresents -ets -o $etl -p Microsoft-Windows-DXGI 0xFFFFFFFFFFFFFFFF 5 | Out-Null

    # GPU : dmon (pas --query-gpu, dont la liste a virgules est decoupee par PowerShell).
    $gpuJob = if (Test-Path $smi) { Start-Job -ArgumentList $smi, $Secondes { param($s, $d) & $s dmon -s u -c ([math]::Max(1, $d - 2)) } }

    $util = @(); $mhzP = @(); $watts = @()
    $fin = (Get-Date).AddSeconds($Secondes)
    while ((Get-Date) -lt $fin) {
        # Classes CIM : leurs noms ne sont pas traduits, ceux des compteurs le sont.
        $tot = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'"
        $util += [double]$tot.PercentProcessorUtility
        $coeurs = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation | Where-Object { $_.Name -notmatch 'Total' }
        $mhzP += ($coeurs | ForEach-Object { [double]$_.ProcessorFrequency * [double]$_.PercentProcessorPerformance / 100 } | Measure-Object -Maximum).Maximum
        $pkg = Get-CimInstance Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*_PKG' }
        if ($pkg) { $watts += [double]$pkg.Power / 1000 }
        Start-Sleep -Milliseconds 900
    }
    & $logman stop DrDxtPresents -ets | Out-Null

    $ev = Get-WinEvent -Path $etl -Oldest -FilterXPath '*[System[EventID=42]]' -ErrorAction SilentlyContinue | Where-Object { $_.ProcessId -eq $jeu.Id }
    $t = $ev | ForEach-Object { $_.TimeCreated.ToUniversalTime().Ticks } | Sort-Object
    $ft = for ($i = 1; $i -lt $t.Count; $i++) { if ($t[$i] -gt $t[$i - 1]) { ($t[$i] - $t[$i - 1]) / 10000.0 } }
    $s = @($ft | Sort-Object); $k = $s.Count
    if ($k -lt 10) { Write-Host "Capture $n : $k image(s) de $Processus, rien a mesurer (le jeu est-il au premier plan ?)"; continue }

    $gpu = if ($gpuJob) { (Receive-Job $gpuJob -Wait -AutoRemoveJob) | Where-Object { $_ -match '^\s+\d' } | ForEach-Object { [double](($_ -split '\s+')[2]) } } else { @() }
    $fps = 1000 / ($s | Measure-Object -Average).Average
    $low1 = 1000 / (($s | Select-Object -Last ([math]::Max(1, [int]($k * 0.01)))) | Measure-Object -Average).Average
    $p99 = $s[[int]($k * 0.99)]
    $lentes = @($s | Where-Object { $_ -gt 33.3 }).Count
    $moy = { param($v) if (@($v).Count) { ($v | Measure-Object -Average).Average } else { [double]::NaN } }
    $ligne = [pscustomobject]@{
        FPS = $fps; Low1 = $low1; P99 = $p99; Lentes = $lentes
        Cpu = (& $moy $util); MhzP = (& $moy $mhzP); Watts = (& $moy $watts); Gpu = (& $moy $gpu)
    }
    "{0,-8} {1}/{2}  FPS {3,6:0.0} | 1% low {4,6:0.0} | p99 {5,5:0.00} ms | >33 ms {6,3} | CPU {7,3:0} % | coeur P max {8,5:0} MHz | paquet {9,5:0.0} W | GPU {10,3:0} %" -f `
        $Etiquette, $n, $Captures, $ligne.FPS, $ligne.Low1, $ligne.P99, $ligne.Lentes, $ligne.Cpu, $ligne.MhzP, $ligne.Watts, $ligne.Gpu
    ("{0:yyyy-MM-dd HH:mm:ss};{1};{2};{3:0.0};{4:0.0};{5:0.00};{6};{7:0};{8:0};{9:0.0};{10:0}" -f (Get-Date), $Etiquette, $Processus,
        $ligne.FPS, $ligne.Low1, $ligne.P99, $ligne.Lentes, $ligne.Cpu, $ligne.MhzP, $ligne.Watts, $ligne.Gpu).Replace(',', '.') |
        Out-File -Append -Encoding utf8 $csv
    $ajoutees++
    Remove-Item $etl -ErrorAction SilentlyContinue
}
Write-Host "$ajoutees capture(s) ajoutee(s) a $csv"
