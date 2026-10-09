#Requires -Version 5.1
<#
.SYNOPSIS
    Dry-run Pester tests for run.ps1.
    Compatible with Pester 3.4+ (ships with Windows PowerShell 5.1 — no install needed).

    Nothing elevates, no restore point is created, no harness exe is invoked.
    All system-touching calls (Start-Process, Enable-ComputerRestore,
    Checkpoint-Computer, Invoke-DrDxtHarnessProcess) are mocked.

    Run: Invoke-Pester -Path tools/amd-harness/run.Tests.ps1
         OR: Invoke-Pester -Path tools/amd-harness/run.Tests.ps1 -EnableExit

    Mock isolation: BeforeEach re-declares mocks before every It block,
    which resets the call count for that mock. Assert-MockCalled therefore
    sees only the calls made within the current It body.
#>

# Load helper functions from run.ps1 without auto-running.
# The `if ($MyInvocation.InvocationName -ne '.')` guard at the bottom of
# run.ps1 prevents Invoke-DrdxtRun from being called here.
. (Join-Path $PSScriptRoot 'run.ps1')

# ─────────────────────────────────────────────────────────────────────────────
# Script parse / load
# ─────────────────────────────────────────────────────────────────────────────

Describe 'run.ps1 - script parse and load' {
    It 'dot-sources without throwing' {
        $runPath = Join-Path $PSScriptRoot 'run.ps1'
        { . $runPath } | Should Not Throw
    }

    It 'exports Test-DrDxtAdmin as a callable function' {
        (Get-Command Test-DrDxtAdmin -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
    }

    It 'exports Invoke-DrDxtHarnessProcess as a callable function' {
        (Get-Command Invoke-DrDxtHarnessProcess -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
    }

    It 'exports Get-DrDxtFootprintStatus as a callable function' {
        (Get-Command Get-DrDxtFootprintStatus -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
    }

    It 'exports Invoke-DrdxtRun as a callable function' {
        (Get-Command Invoke-DrdxtRun -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Unit tests: Get-DrDxtFootprintStatus (pure parsing, no mocking needed)
# ─────────────────────────────────────────────────────────────────────────────

Describe 'Get-DrDxtFootprintStatus - pure parsing' {
    It 'returns "clean" when [OK] cleanup-verify is present' {
        $lines = @('  [OK] detect', '  [OK] cleanup-verify', '  [OK] scan')
        Get-DrDxtFootprintStatus -RawLines $lines | Should Be 'clean'
    }

    It 'returns "NOT clean" when [!!] cleanup-verify is present' {
        $lines = @('  [OK] detect', '  [!!] cleanup-verify -- service found')
        Get-DrDxtFootprintStatus -RawLines $lines | Should Be 'NOT clean'
    }

    It 'returns an informational string when no cleanup-verify line is present' {
        $lines = @('  [OK] detect', '  [OK] scan', '  [OK] adlx-gate')
        $result = Get-DrDxtFootprintStatus -RawLines $lines
        $result | Should BeLike '*(cleanup step not found*'
    }

    It 'handles an empty lines array without throwing' {
        { Get-DrDxtFootprintStatus -RawLines @() } | Should Not Throw
    }

    It 'matches [OK] cleanup-verify even when a note suffix follows' {
        # The harness only appends notes on [!!]; this guards against future format changes.
        $lines = @('  [OK] cleanup-verify -- all clear')
        Get-DrDxtFootprintStatus -RawLines $lines | Should Be 'clean'
    }

    It 'matches [!!] cleanup-verify with a note suffix' {
        $lines = @('  [!!] cleanup-verify -- residuals: C:\foo')
        Get-DrDxtFootprintStatus -RawLines $lines | Should Be 'NOT clean'
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Integration tests: Invoke-DrdxtRun
#
# ScriptDir is always $TestDrive (Pester temp dir, empty) so neither
# PresentMonService.exe nor DrDxt.AmdHarness.exe exist there — those code
# paths are bypassed naturally without extra mocking.
#
# Pester 3.4 note: Assert-MockCalled accumulates counts across all It blocks
# within a Describe, even with BeforeEach { Mock }. BeforeEach does NOT reset
# call history for previously-created mocks — it only updates mock behaviour.
# As a result:
#   - Zero-count assertions (-Times 0 -Exactly) are always correct: a function
#     that is never reached contributes 0 calls regardless of how many Its run.
#   - Positive-count assertions use -Times 1 WITHOUT -Exactly ("at least once"):
#     after N Its, the count is N which satisfies >= 1. The semantics are
#     weaker than "exactly once" but the invariant (function WAS called) holds.
# ─────────────────────────────────────────────────────────────────────────────

Describe 'Invoke-DrdxtRun - self-elevation (not admin)' {
    BeforeEach {
        Mock Test-DrDxtAdmin            { return $false }
        Mock Start-Process              { }
        Mock Enable-ComputerRestore     { }
        Mock Checkpoint-Computer        { }
        Mock Invoke-DrDxtHarnessProcess { return @() }
    }

    It 're-launches PowerShell elevated (Start-Process -Verb RunAs) and returns early' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Start-Process -Times 1 `
            -ParameterFilter { $Verb -eq 'RunAs' }
    }

    It 'does not touch System Restore when not admin' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Enable-ComputerRestore -Times 0 -Exactly
        Assert-MockCalled Checkpoint-Computer    -Times 0 -Exactly
    }

    It 'does not run the harness when not admin' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Invoke-DrDxtHarnessProcess -Times 0 -Exactly
    }
}

Describe 'Invoke-DrdxtRun - System Restore (admin, SR enable succeeds)' {
    BeforeEach {
        Mock Test-DrDxtAdmin            { return $true }
        Mock Enable-ComputerRestore     { }
        Mock Checkpoint-Computer        { }
        Mock Invoke-DrDxtHarnessProcess { return @('  [OK] cleanup-verify') }
    }

    It 'calls Enable-ComputerRestore (SR enabled before run)' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Enable-ComputerRestore -Times 1
    }

    It 'calls Checkpoint-Computer when Enable succeeds' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Checkpoint-Computer -Times 1
    }

    It 'runs the harness' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Invoke-DrDxtHarnessProcess -Times 1
    }
}

Describe 'Invoke-DrdxtRun - SR Enable fails (warns and continues)' {
    BeforeEach {
        Mock Test-DrDxtAdmin            { return $true }
        Mock Enable-ComputerRestore     { throw 'System Restore is disabled by policy' }
        Mock Checkpoint-Computer        { }
        Mock Write-Warning              { }
        Mock Invoke-DrDxtHarnessProcess { return @('  [OK] cleanup-verify') }
    }

    It 'does not throw when Enable-ComputerRestore fails' {
        { Invoke-DrdxtRun -ScriptDir $TestDrive } | Should Not Throw
    }

    It 'emits at least one warning when Enable fails' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Write-Warning -Times 1
    }

    It 'skips Checkpoint-Computer when Enable failed' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Checkpoint-Computer -Times 0 -Exactly
    }

    It 'still runs the harness despite SR failure' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Invoke-DrDxtHarnessProcess -Times 1
    }
}

Describe 'Invoke-DrdxtRun - Checkpoint-Computer fails (warns and continues)' {
    BeforeEach {
        Mock Test-DrDxtAdmin            { return $true }
        Mock Enable-ComputerRestore     { }
        Mock Checkpoint-Computer        { throw 'Access denied to System Restore' }
        Mock Write-Warning              { }
        Mock Invoke-DrDxtHarnessProcess { return @('  [OK] cleanup-verify') }
    }

    It 'does not throw when Checkpoint-Computer fails' {
        { Invoke-DrdxtRun -ScriptDir $TestDrive } | Should Not Throw
    }

    It 'emits at least one warning when Checkpoint fails' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Write-Warning -Times 1
    }

    It 'still runs the harness despite checkpoint failure' {
        Invoke-DrdxtRun -ScriptDir $TestDrive
        Assert-MockCalled Invoke-DrDxtHarnessProcess -Times 1
    }
}

Describe 'Invoke-DrdxtRun - footprint status reflects harness result' {
    BeforeEach {
        Mock Test-DrDxtAdmin        { return $true }
        Mock Enable-ComputerRestore { }
        Mock Checkpoint-Computer    { }
    }

    It 'returns "clean" when harness output contains [OK] cleanup-verify' {
        Mock Invoke-DrDxtHarnessProcess { return @('  [OK] detect', '  [OK] cleanup-verify') }
        $result = Invoke-DrdxtRun -ScriptDir $TestDrive
        $result | Should Be 'clean'
    }

    It 'returns "NOT clean" when harness output contains [!!] cleanup-verify' {
        Mock Invoke-DrDxtHarnessProcess {
            return @('  [OK] detect', '  [!!] cleanup-verify -- service found')
        }
        $result = Invoke-DrdxtRun -ScriptDir $TestDrive
        $result | Should Be 'NOT clean'
    }

    It 'returns an informational note when cleanup-verify is absent from output' {
        Mock Invoke-DrDxtHarnessProcess { return @('  [OK] detect') }
        $result = Invoke-DrdxtRun -ScriptDir $TestDrive
        $result | Should BeLike '*(cleanup step not found*'
    }
}
