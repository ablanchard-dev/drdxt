#Requires -Version 5.1
<#
.SYNOPSIS
    Compile les shaders de l'interface (.hlsl -> .ps, ps_3_0) avec fxc du Windows SDK.

.DESCRIPTION
    Les .ps compilés sont versionnés à côté de leur source : la construction de l'application
    n'exige donc pas le SDK. Relancer ce script après toute modification d'un .hlsl.
#>
$ErrorActionPreference = 'Stop'

$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc) { throw "fxc.exe introuvable : installer le Windows SDK." }

Get-ChildItem $PSScriptRoot -Filter *.hlsl | ForEach-Object {
    $sortie = [System.IO.Path]::ChangeExtension($_.FullName, '.ps')
    & $fxc.FullName /nologo /T ps_3_0 /E main /O3 /Fo $sortie $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "compilation echouee : $($_.Name)" }
    "compile : $($_.Name) -> $([System.IO.Path]::GetFileName($sortie))"
}
