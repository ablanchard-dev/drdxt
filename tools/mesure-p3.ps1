# =============================================================================
# MESURE P3 — la preuve en jeu, en deux double-clics.
#
# Le produit promet un gain FPS reel. Cette promesse n'avait jamais ete mesuree :
# le protocole existait sur le papier, personne ne l'avait lance. Ce script est la
# pour qu'il n'y ait plus rien a preparer.
#
# COMMENT ON S'EN SERT (aucune elevation, aucun UAC) :
#   1. Lance ton jeu, mets-toi dans une situation representative (une vraie partie,
#      pas le menu). Double-clique ce script -> il capture l'ETAT AVANT.
#   2. Applique tes reglages (DrDXT, ou a la main).
#   3. Remets-toi dans la MEME situation. Double-clique encore -> il capture l'APRES
#      et affiche le verdict.
#
# Une 3e execution repart d'une nouvelle mesure AVANT (le cycle recommence).
#
# CE QUE CE SCRIPT NE FAIT PAS, et c'est deliberе :
#   * il ne change AUCUN reglage -- il ne fait que mesurer ;
#   * il n'invente aucun verdict : sous la marge d'erreur, il dit « indistinguable »,
#     jamais « gain ». Deux mesures qui se recouvrent ne prouvent rien.
# =============================================================================

$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot
$smoke  = Join-Path $repo 'tools\smoke\bin\Debug\net8.0-windows\smoke.exe'
$store  = Join-Path $repo 'tools\p3'
$before = Join-Path $store 'avant.json'

# Duree et repetitions : 3 passes de 60 s. MESURE le 01/09 sur des trames reelles :
# a 3 x 10 s la marge d'erreur vaut +/- 33 FPS (seuil 22 %, rien de prouvable) ; a
# 3 x 60 s elle tombe entre +/- 2,1 et +/- 7,9 FPS selon le triplet. Ne pas raccourcir.
#
# CE QUE LA DUREE NE CORRIGE PAS, mesure le meme soir sur 595 s immobiles :
#   * une MORT dans la fenetre => 15 trames > 100 ms groupees sur ~20 s => le 0,1 % low
#     tombe de 54 a 4 FPS et la marge monte a +/- 21,6. Une capture qui couvre une mort
#     est a jeter -- on ne filtre PAS les morts, ce serait embellir le chiffre.
#   * le FPS DERIVE avec la phase de partie : 136 FPS aux 4 premieres minutes, 148 a la
#     dixieme (+9 %), sans que rien ne change sur la machine. AVANT et APRES doivent
#     etre pris a la MEME phase, sinon la derive avale le gain qu'on veut prouver.
$secs = 60
$runs = 3

function Fin($msg) { Write-Host ""; Write-Host $msg; Write-Host ""; Read-Host "Entree pour fermer" | Out-Null; exit }

if (-not (Test-Path $smoke)) {
    Fin "smoke.exe est introuvable. Compile-le d'abord :`n  dotnet build tools\smoke\smoke.csproj"
}
New-Item -ItemType Directory -Path $store -Force | Out-Null

# --- Un seul PresentMon a la fois -------------------------------------------
# Constate le 27/08/2026 : deux services PresentMon de versions differentes
# (celui de DrDXT en 2.4.1, celui d'Intel en 2.5.1) publient le MEME tube nomme.
# La capture echouait alors 3 fois sur 5, sans motif visible, avec
# PM_STATUS_PIPE_ERROR. Une mesure avant/apres dont une moitie echoue au hasard
# ne prouve rien -- autant le dire avant que tu lances une partie pour rien.
$rivaux = @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
    Where-Object { $_.PathName -match 'PresentMon' -and $_.State -eq 'Running' -and $_.Name -ne 'PresentMonService' })
if ($rivaux) {
    $noms = ($rivaux | ForEach-Object { "$($_.Name)" }) -join ', '
    Fin ("Un autre service PresentMon tourne en meme temps que celui de DrDXT : $noms`n`n" +
         "Les deux se partagent le meme tube nomme et la mesure echoue environ une fois`n" +
         "sur deux, au hasard. Arrete-le le temps de mesurer, dans une fenetre admin :`n`n" +
         "  Stop-Service $($rivaux[0].Name)`n`n" +
         "...puis relance ce script. Pense a le redemarrer apres :`n" +
         "  Start-Service $($rivaux[0].Name)")
}

# --- Trouver le jeu -----------------------------------------------------------
# On ne demande PAS de chercher un PID a la main : le script propose ce qu'il voit.
$known = @('cod','ModernWarfare','BlackOps','r5apex','FortniteClient-Win64-Shipping',
           'VALORANT-Win64-Shipping','cs2','Overwatch','RainbowSix','csgo','Warzone')

$candidates = Get-Process | Where-Object {
    $_.MainWindowHandle -ne 0 -and $_.Id -ne $PID -and
    ($known -contains $_.ProcessName -or $_.WorkingSet64 -gt 800MB)
} | Sort-Object WorkingSet64 -Descending

if (-not $candidates) {
    Fin "Aucun jeu detecte. Lance le jeu, mets-toi en partie, puis relance ce script."
}

$game = $candidates[0]
if ($candidates.Count -gt 1) {
    Write-Host "Jeux possibles :"
    for ($i = 0; $i -lt [Math]::Min($candidates.Count, 5); $i++) {
        "  [{0}] {1}  (PID {2}, {3:N0} Mo)" -f $i, $candidates[$i].ProcessName, $candidates[$i].Id, ($candidates[$i].WorkingSet64/1MB)
    }
    $pick = Read-Host "Numero (Entree = 0)"
    if ($pick -match '^\d+$' -and [int]$pick -lt $candidates.Count) { $game = $candidates[[int]$pick] }
}

$phase = if (Test-Path $before) { 'APRES' } else { 'AVANT' }
Write-Host ""
Write-Host "=== MESURE $phase : $($game.ProcessName) (PID $($game.Id)) ==="
Write-Host "$runs passes de $secs s. Joue NORMALEMENT pendant la mesure."
Write-Host ""

# --- Capturer -----------------------------------------------------------------
$raw = & $smoke $game.Id $secs $runs 2>&1 | Out-String
Write-Host $raw

$m = [regex]::Match($raw, 'RESULT OK: frames=(?<f>\d+) avgFps=(?<avg>[\d.,]+) p1Low=(?<p1>[\d.,]+) p01Low=(?<p01>[\d.,]+).*?moE=±(?<moe>[\d.,]+)')
if (-not $m.Success) {
    $fail = [regex]::Match($raw, 'RESULT FAIL: (?<code>\S+)')
    $why  = if ($fail.Success) { $fail.Groups['code'].Value } else { 'sortie non reconnue' }
    Fin "Mesure NON aboutie ($why). Rien n'a ete enregistre -- on ne garde pas une demi-mesure.`nSi le jeu tournait bien, verifie que le service PresentMon est demarre :`n  Start-Service PresentMonService   (fenetre admin)"
}

function D($s) { [double]::Parse(($s -replace ',', '.'), [Globalization.CultureInfo]::InvariantCulture) }
$now = [ordered]@{
    phase = $phase; process = $game.ProcessName; pid = $game.Id
    horodatage = (Get-Date).ToString('s')
    frames = [int]$m.Groups['f'].Value
    avgFps = D $m.Groups['avg'].Value
    p1Low  = D $m.Groups['p1'].Value
    p01Low = D $m.Groups['p01'].Value
    moE    = D $m.Groups['moe'].Value
    secondes = $secs; passes = $runs
}

if ($phase -eq 'AVANT') {
    $now | ConvertTo-Json | Set-Content -Path $before -Encoding utf8
    Fin ("AVANT enregistre : {0:F1} FPS moyens, {1:F1} en 1% low (±{2:F2}).`n`n" -f $now.avgFps, $now.p1Low, $now.moE +
         "Applique tes reglages, remets-toi dans la MEME situation, et relance ce script.")
}

# --- Comparer -----------------------------------------------------------------
$b = Get-Content $before -Raw | ConvertFrom-Json
$after = Join-Path $store ("apres-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$now | ConvertTo-Json | Set-Content -Path $after -Encoding utf8

$dAvg = $now.avgFps - $b.avgFps
$dP1  = $now.p1Low  - $b.p1Low
# La marge d'erreur des DEUX mesures compte : un ecart plus petit que leur somme
# peut n'etre que du bruit. On refuse de le nommer « gain ».
$seuil = $b.moE + $now.moE

Write-Host ""
Write-Host "================ VERDICT ================"
"  AVANT : {0,6:F1} FPS moyens | {1,6:F1} en 1% low   ({2})" -f $b.avgFps, $b.p1Low, $b.horodatage
"  APRES : {0,6:F1} FPS moyens | {1,6:F1} en 1% low   ({2})" -f $now.avgFps, $now.p1Low, $now.horodatage
"  ECART : {0,+6:F1} FPS        | {1,+6:F1} en 1% low" -f $dAvg, $dP1
"  Marge d'erreur cumulee : ±{0:F2} FPS" -f $seuil
Write-Host ""

if ($b.process -ne $now.process) {
    Write-Host "  ATTENTION : jeux differents ($($b.process) puis $($now.process)). Comparaison sans valeur."
} elseif ([Math]::Abs($dAvg) -le $seuil) {
    Write-Host "  INDISTINGUABLE : l'ecart est sous la marge d'erreur. Ce n'est PAS un gain,"
    Write-Host "  et ce n'est pas une perte -- les deux mesures se recouvrent."
} elseif ($dAvg -gt 0) {
    "  GAIN MESURE : +{0:F1} FPS ({1:P1}), au-dela de la marge d'erreur." -f $dAvg, ($dAvg / $b.avgFps)
} else {
    "  PERTE MESUREE : {0:F1} FPS ({1:P1}). Les reglages ont coute des images." -f $dAvg, ($dAvg / $b.avgFps)
}

Write-Host ""
Write-Host "  Mesures gardees : $store"
Write-Host "  (supprime avant.json pour repartir d'une nouvelle mesure AVANT)"
Remove-Item $before -ErrorAction SilentlyContinue
Fin "Cycle termine. La prochaine execution repartira d'une mesure AVANT."
