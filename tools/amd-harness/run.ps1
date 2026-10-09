#Requires -Version 5.1
<#
.SYNOPSIS
    DrDXT AMD Harness orchestrator — run this on the test machine.
    Self-elevates via UAC, takes a System Restore point, runs the harness,
    streams the report to console + a timestamped .txt, and prints a final
    "footprint: clean / NOT clean" line.
.NOTES
    Compatible with Windows PowerShell 5.1. No external modules required.
    Run: Right-click -> Run with PowerShell
         OR: powershell -ExecutionPolicy Bypass -File run.ps1
#>

# ─────────────────────────────────────────────────────────────────────────────
# Helpers — defined at script scope so Pester can mock them after dot-sourcing
# ─────────────────────────────────────────────────────────────────────────────

function Test-DrDxtAdmin {
    <#.SYNOPSIS Returns $true when the current process is running as Administrator.#>
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-DrDxtHarnessProcess {
    <#
    .SYNOPSIS
        Runs DrDxt.AmdHarness.exe (or dotnet run as a dev fallback).
        Streams output to the console AND saves it to $LogFile via Tee-Object.
        Returns the captured stdout lines for post-run parsing.
    #>
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$LogFile
    )
    if (Test-Path $ExePath) {
        return & "$ExePath" | Tee-Object -FilePath $LogFile
    }
    # Dev fallback: csproj lives one level up from where the published exe would be
    $projDir  = Split-Path $ExePath -Parent
    $projFile = Join-Path $projDir 'DrDxt.AmdHarness\DrDxt.AmdHarness.csproj'
    if (Test-Path $projFile) {
        Write-Host '(Published exe not found — falling back to dotnet run; first build may be slow)'
        return dotnet run --project "$projFile" | Tee-Object -FilePath $LogFile
    }
    Write-Warning 'Neither the published exe nor the project file was found next to the script.'
    return @()
}

function Get-DrDxtFootprintStatus {
    <#
    .SYNOPSIS
        Parses harness stdout lines for the cleanup-verify step outcome.
        Returns 'clean', 'NOT clean', or an informational note string.
    #>
    [OutputType([string])]
    param([string[]]$RawLines)
    foreach ($line in $RawLines) {
        if ($line -match '\[OK\]\s+cleanup-verify')  { return 'clean' }
        if ($line -match '\[!!\]\s+cleanup-verify') { return 'NOT clean' }
    }
    return '(cleanup step not found in output — see the report file for details)'
}

# ─────────────────────────────────────────────────────────────────────────────
# Main orchestrator
# ─────────────────────────────────────────────────────────────────────────────

function Invoke-DrdxtRun {
    [CmdletBinding()]
    param(
        # Directory that contains the harness exe and this script.
        # Override in tests to keep everything inside Pester's $TestDrive.
        [string]$ScriptDir = $PSScriptRoot
    )

    # ── 1. Self-elevate ───────────────────────────────────────────────────────
    if (-not (Test-DrDxtAdmin)) {
        Write-Host 'Not running as Administrator — re-launching elevated via UAC...'
        $scriptPath = Join-Path $ScriptDir 'run.ps1'
        $elevArgs   = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
        Start-Process powershell -Verb RunAs -ArgumentList $elevArgs
        return
    }

    # ── 2. System Restore safety net ──────────────────────────────────────────
    # Creates a 'Before DrDXT test' restore point as an EXTRA safety net.
    # The harness reverts its own GPU offset and safe-tweak changes regardless —
    # System Restore is not the primary rollback mechanism here.
    $drive    = $env:SystemDrive
    $enableOk = $false

    try {
        Enable-ComputerRestore -Drive $drive -ErrorAction Stop
        $enableOk = $true
    } catch {
        Write-Warning "Cannot ensure System Restore is enabled on ${drive}: $_"
        Write-Warning 'The harness reverts its own changes — System Restore is an extra safety net only.'
    }

    if ($enableOk) {
        try {
            Checkpoint-Computer -Description 'Before DrDXT test' -RestorePointType 'APPLICATION_INSTALL' -ErrorAction Stop
            Write-Host "System Restore point created: 'Before DrDXT test'."
        } catch {
            Write-Warning "Could not create System Restore point: $_"
            Write-Warning 'Continuing — the harness still reverts its own changes.'
        }
    }

    # ── 3. Optional: start PresentMonService for benchmark frame capture ───────
    # run.ps1 is already elevated so it can spawn the service process.
    # The harness bench steps degrade cleanly (non-fatal) when the service is absent.
    $pmPath    = Join-Path $ScriptDir 'PresentMonService.exe'
    $pmStarted = $false

    if (Test-Path $pmPath) {
        $pmRunning = Get-Process -Name 'PresentMonService' -ErrorAction SilentlyContinue
        if ($null -eq $pmRunning) {
            try {
                Start-Process -FilePath $pmPath -NoNewWindow -ErrorAction Stop
                $pmStarted = $true
                Write-Host 'PresentMonService.exe started (benchmark steps enabled).'
            } catch {
                Write-Warning "Could not start PresentMonService.exe: $_"
                Write-Warning 'Benchmark steps will degrade cleanly (non-fatal).'
            }
        } else {
            Write-Host 'PresentMonService.exe is already running.'
        }
    } else {
        Write-Host 'PresentMonService.exe not found next to script — benchmark steps will degrade cleanly.'
    }

    # ── 4. Run the harness ────────────────────────────────────────────────────
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logFile   = Join-Path $ScriptDir "harness-run-$timestamp.txt"
    $exePath   = Join-Path $ScriptDir 'DrDxt.AmdHarness.exe'

    Write-Host ''
    Write-Host '── DrDXT AMD Harness ──────────────────────────────────────────────'
    $rawLines = Invoke-DrDxtHarnessProcess -ExePath $exePath -LogFile $logFile
    if ($null -eq $rawLines) { $rawLines = @() }
    Write-Host '── End of harness output ──────────────────────────────────────────'
    Write-Host ''

    # ── 5. Stop PresentMon if we started it (leave machine as-found) ──────────
    if ($pmStarted) {
        try {
            Stop-Process -Name 'PresentMonService' -Force -ErrorAction SilentlyContinue
            Write-Host 'PresentMonService.exe stopped.'
        } catch {}
    }

    # ── 6. Surface footprint status ────────────────────────────────────────────
    $status = Get-DrDxtFootprintStatus -RawLines $rawLines
    Write-Host "footprint: $status"
    Write-Host "Report saved to: $logFile"

    return $status
}

# ── Script entry point ────────────────────────────────────────────────────────
# Skipped when dot-sourced (Pester loads functions without running the main body).
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-DrdxtRun
}
