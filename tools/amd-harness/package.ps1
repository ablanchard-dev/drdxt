#Requires -Version 5.1
# OBSOLÈTE (07/10/2026) : ce script fabrique le zip de juillet (session d'écriture de fréquence et de tension, point de
# restauration), retirée par la règle du 29/09. Utiliser paquet-rapport.ps1.
<#
.SYNOPSIS
    Builds the portable DrDXT AMD Harness zip for distribution.
    Run on a dev machine (dotnet SDK required).
    The resulting zip needs no SDK on the target machine (self-contained win-x64).
.DESCRIPTION
    Steps:
      1. dotnet publish --self-contained win-x64 into tools/amd-harness/publish/
      2. Verify PresentMonAPI2.dll + PresentMonAPI2Loader.dll are in publish output
         (they come from DrDxt.Measurement.csproj CopyToOutputDirectory — no manual copy needed)
      3. Bundle PresentMonService.exe from third_party/presentmon/ (optional for bench)
      4. Bundle run.ps1 and write README.txt
      5. Compress publish/ into tools/amd-harness/dist/DrDxt-AmdHarness-<date>.zip

    NOT bundled: amdadlx64.dll
      This DLL ships with the AMD Adrenalin driver suite and is already present on
      any AMD machine with the driver installed. Bundling it is unnecessary and
      potentially a license violation — the harness loads it from the driver path.

    ALSO NOT bundled: any bin/, obj/ intermediate build artifacts.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir   = $PSScriptRoot                                     # tools/amd-harness/
$toolsDir    = Split-Path $scriptDir -Parent                     # tools/
$repoRoot    = Split-Path $toolsDir -Parent                      # repo root
$harnessProj = Join-Path $scriptDir 'DrDxt.AmdHarness\DrDxt.AmdHarness.csproj'
$publishDir  = Join-Path $scriptDir 'publish'
$distDir     = Join-Path $scriptDir 'dist'
$pmDir       = Join-Path $repoRoot 'third_party\presentmon'

# ── 1. Publish self-contained win-x64 ────────────────────────────────────────
Write-Host "Publishing $harnessProj"
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : win-x64 (self-contained)"
Write-Host "  Output        : $publishDir"
Write-Host ''

dotnet publish "$harnessProj" -c $Configuration -r win-x64 --self-contained -o "$publishDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

# ── 2. Verify PresentMon API DLLs landed in the publish output ───────────────
# DrDxt.Measurement.csproj declares these with CopyToOutputDirectory=PreserveNewest.
# dotnet publish propagates CopyToOutputDirectory items from all referenced projects,
# so they should already be here — this is a verification step, not a manual copy.
$requiredDlls = @('PresentMonAPI2.dll', 'PresentMonAPI2Loader.dll')
foreach ($dll in $requiredDlls) {
    $target = Join-Path $publishDir $dll
    if (Test-Path $target) {
        Write-Host "  [OK] $dll"
    } else {
        Write-Warning "$dll not found in publish output. Check DrDxt.Measurement.csproj CopyToOutputDirectory items."
    }
}

# ── 3. Bundle PresentMonService.exe ──────────────────────────────────────────
# Source: third_party/presentmon/ (vendored PresentMon 2.4.1, MIT license).
# The service must run elevated for benchmark steps to capture frame data.
# run.ps1 starts it automatically if present; bench steps degrade cleanly without it.
# This is the ONLY presentmon binary not covered by the csproj copy steps.
$pmService = Join-Path $pmDir 'PresentMonService.exe'
if (Test-Path $pmService) {
    Copy-Item $pmService -Destination $publishDir -Force
    Write-Host "  [OK] PresentMonService.exe (start elevated for benchmark support)"
} else {
    Write-Warning "PresentMonService.exe not found at $pmService"
    Write-Warning 'Benchmark steps will degrade cleanly without it — all other steps are unaffected.'
}

# ── 4. Bundle run.ps1 ────────────────────────────────────────────────────────
Copy-Item (Join-Path $scriptDir 'run.ps1') -Destination $publishDir -Force
Write-Host "  [OK] run.ps1"

# ── 5. Write README.txt ──────────────────────────────────────────────────────
$readmePath = Join-Path $publishDir 'README.txt'
@'
DrDXT AMD Harness - portable one-shot test
==========================================

WHAT IT DOES
  Runs a single session on your AMD machine. It never installs services,
  scheduled tasks, or drivers. Steps in order:

    1. Detects hardware (CPU, GPU, memory configuration).
    2. Reads current system settings (power plan, XMP, misc tuning state).
    3. Runs optional benchmark (PresentMon frame capture, if game is running).
    4. Probes AMD GPU tuning via ADLX (read-only gate check).
    5. Writes a small test clock offset (optionally benchmarks it) AND a tiny
       voltage offset, reads back whether each was honored by the driver, then
       reverts both immediately. Nothing is kept.
    6. Reverts all applied changes (LIFO, always runs even if earlier steps fail).
    7. Verifies the footprint is clean (no service, no scheduled task, no residuals).

  A System Restore point ("Before DrDXT test") is created as an extra safety
  net before the run. The harness reverts its own changes regardless.

REQUIREMENTS
  - Windows 10 or 11
  - AMD GPU with Adrenalin driver installed (provides ADLX support)
  - amdadlx64.dll is NOT included in this package: it ships with your AMD
    Adrenalin driver and is already on your machine. The harness loads it
    from the driver's install path automatically.
  - No .NET runtime needed (this build is self-contained).

BENCHMARK SUPPORT (optional)
  Frame capture requires PresentMonService.exe running as Administrator.
  run.ps1 starts it automatically if the file is present next to the script,
  and stops it when the harness finishes. Without it, benchmark steps are
  skipped cleanly and every other step still runs normally.

HOW TO RUN
  Right-click run.ps1 -> Run with PowerShell
  Or from an elevated PowerShell prompt:
      powershell -ExecutionPolicy Bypass -File run.ps1

  The script self-elevates via a UAC prompt if needed.

OUTPUT
  All console output is saved to harness-run-YYYYMMDD-HHmmss.txt in the
  same folder as the script.

  The final line printed is one of:
      footprint: clean      -- nothing was left behind (expected)
      footprint: NOT clean  -- something unexpected remains (see the log)
'@ | Out-File -FilePath $readmePath -Encoding utf8
Write-Host "  [OK] README.txt"

# ── 6. Compress into dist/ ────────────────────────────────────────────────────
if (-not (Test-Path $distDir)) {
    $null = New-Item -ItemType Directory -Path $distDir -Force
}

$zipName = "DrDxt-AmdHarness-$(Get-Date -Format 'yyyyMMdd').zip"
$zipPath = Join-Path $distDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath

$fileCount = (Get-ChildItem $publishDir | Measure-Object).Count
Write-Host ''
Write-Host "Package ready: $zipPath"
Write-Host "  Files bundled: $fileCount"
Write-Host ''
Write-Host 'Send the zip to the test machine. Have the friend:'
Write-Host '  1. Extract the zip'
Write-Host '  2. Right-click run.ps1 -> Run with PowerShell'
Write-Host '  3. Accept the UAC prompt'
