#Requires -Version 5.1
<#
.SYNOPSIS
    07/10/2026 : fabrique le zip du RAPPORT EN LECTURE SEULE pour un joueur (machine AMD ou autre).
    (Le zip de juillet, session d'écriture retirée par la règle du 29/09, et son script sont supprimés.)
    Dossier publié, pas un exe en un seul fichier : un exe qui se décompresse dans %TEMP% au lancement
    est ce que les antivirus aiment le moins (revue n° 8).
#>
[CmdletBinding()]
# -Validation : le zip de la validation complète (écrit puis remet d'origine), à envoyer peu avant la mise en public.
param([switch]$Validation,
      [string]$Sortie = (Join-Path $env:USERPROFILE "Downloads\DrDXT-$(if ($Validation) { 'Validation' } else { 'Test' })-$(Get-Date -Format yyyy-MM-dd).zip"))
$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
$pub = Join-Path $env:TEMP 'drdxt-banc-publie'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish (Join-Path $ici 'DrDxt.AmdHarness\DrDxt.AmdHarness.csproj') -c Release -r win-x64 --self-contained -o $pub -nodeReuse:false -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué ($LASTEXITCODE)" }
Rename-Item (Join-Path $pub 'DrDxt.AmdHarness.exe') 'DrDXT-Test.exe'
# 10/10/2026 (revue de sécurité) : le lanceur natif reçoit l'élévation et démarre le banc avec un environnement reconstruit.
# NativeAOT cherche vswhere.exe sur le PATH (cible Microsoft.NETCore.Native) : le dossier de l'installeur Visual Studio y passe.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$lanceur = Join-Path $env:TEMP 'drdxt-lanceur-publie'
if (Test-Path $lanceur) { Remove-Item $lanceur -Recurse -Force }
dotnet publish (Join-Path $ici '..\..\src\DrDxt.Lanceur\DrDxt.Lanceur.csproj') -c Release -r win-x64 -o $lanceur -nodeReuse:false -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (lanceur) a échoué ($LASTEXITCODE)" }
Copy-Item (Join-Path $lanceur 'DrDxt.Lanceur.exe') $pub
Remove-Item $lanceur -Recurse -Force
Get-ChildItem $pub -Filter *.pdb | Remove-Item
if ($Validation) { 'LISEZ-MOI-VALIDATION.txt', 'VALIDER.cmd', 'REMETTRE.cmd' | ForEach-Object { Copy-Item (Join-Path $ici $_) $pub } }
else { Copy-Item (Join-Path $ici 'LISEZ-MOI.txt') $pub }
if (Test-Path $Sortie) { Remove-Item $Sortie -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $Sortie
Write-Host "Zip prêt : $Sortie ($([math]::Round((Get-Item $Sortie).Length / 1MB)) Mo)"
