# A/B PROPRE sur le bench deterministe (pas besoin de jouer).
# Lance-moi : un UAC (-> OUI). ~20s. Mesure le score GPU a STOCK, +75, +100, puis revert.
# Le bench DETECTE lui-meme l'instabilite (checksum/device-lost) -> teste aussi la stabilite
# de chaque offset. Resultats ecrits dans tools\bench_ab.txt, a relire apres le passage.

$ErrorActionPreference = 'Continue'

# --- auto-elevation (r2apply a besoin d'admin ; r2bench tourne tres bien en eleve aussi) ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  Write-Host "Passage en ELEVE (UAC -> clique OUI). ~20s, pas besoin de jouer."
  Start-Process -Verb RunAs powershell -ArgumentList '-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"" -Wait
  Write-Host "Termine - resultats dans tools\bench_ab.txt."
  return
}

# ===== partie ELEVEE =====
$repo    = Split-Path -Parent $PSScriptRoot   # ce script vit dans <depot>\tools
$r2bench = "$repo\tools\r2bench\bin\Release\net8.0-windows\r2bench.exe"
$r2apply = "$repo\tools\r2apply\bin\Release\net8.0-windows\r2apply.exe"
$r2probe = "$repo\tools\r2probe\bin\Release\net8.0-windows\r2probe.exe"
if (-not (Test-Path $r2probe)) { $r2probe = "$repo\tools\r2probe\bin\Debug\net8.0-windows\r2probe.exe" }
$out  = "$repo\tools\bench_ab.txt"
$secs = 5

foreach ($x in @($r2bench, $r2apply, $r2probe)) { if (-not (Test-Path $x)) { Write-Host "MANQUE: $x"; Start-Sleep 3; return } }
Remove-Item $out -ErrorAction SilentlyContinue

function Section($t) { "`n=== $t ===" | Tee-Object -FilePath $out -Append; Write-Host "=== $t ===" }
function Bench($label) { Section "BENCH $label"; & $r2bench 0 $secs | Tee-Object -FilePath $out -Append }
function Apply($mhz)   { Section "APPLY +$mhz MHz"; & $r2apply 0 $mhz | Tee-Object -FilePath $out -Append }

"DrDXT bench A/B  $(Get-Date)" | Tee-Object -FilePath $out -Append
Bench "STOCK (0 MHz)"
Apply 75
Bench "+75 MHz"
Apply 100
Bench "+100 MHz"
Section "REVERT a 0"
& $r2apply 0 0 100 | Tee-Object -FilePath $out -Append
& $r2probe         | Tee-Object -FilePath $out -Append
Section "FINI - resultats dans tools\bench_ab.txt"
Start-Sleep 2
