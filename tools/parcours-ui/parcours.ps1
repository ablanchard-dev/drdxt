#Requires -Version 5.1
<#
.SYNOPSIS
    Parcourt chaque page de DrDXT sur une machine rejouée et relève, par UI Automation, ce qu'un lecteur d'écran lirait.

.DESCRIPTION
    Lance le build Debug hors écran (« hors-ecran scan-auto deplier ») sur une machine de tests/Shared/machines. Son
    propre visiteur passe chaque page, déplie les listes, photographie, puis ferme l'App. Pendant ce temps, ce script lit
    l'arbre UI Automation de la fenêtre et relève : les contrôles interactifs sans nom accessible, les clés de traduction
    non résolues, les gabarits non remplis ({0}), les valeurs cassées (NaN, noms de types .NET, champs d'un record) et,
    en anglais, le texte resté en français. Lecture seule : aucun Invoke, aucun Toggle, aucun clic. Le rejeu écrit ses
    données dans un dossier jetable (MachineDeTest) ; rien ne touche ce PC.

    Le français relevé en anglais vient souvent des machines enregistrées elles-mêmes (leurs constats ont été relevés en
    français) : il se juge à la lecture, ligne par ligne.

.EXAMPLE
    dotnet build src\DrDxt.App -c Debug
    powershell -File tools\parcours-ui\parcours.ps1 -Machine nvidia-rtx3060-i5-12400f -Langue en-US
#>
param(
    [Parameter(Mandatory)] [string]$Machine,
    [string]$Langue = 'fr-FR',
    [string]$Exe = '',
    [string]$Sortie = (Join-Path $env:TEMP 'drdxt-parcours'),
    [int]$DelaiMax = 420
)
$ErrorActionPreference = 'Stop'
# Sous PowerShell 5.1, $PSScriptRoot est vide dans les valeurs par défaut des paramètres : le chemin se calcule ici.
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\..\src\DrDxt.App\bin\Debug\net10.0-windows\DrDxt.App.exe' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$photos = Join-Path $env:TEMP 'drdxt-photos'
if (Test-Path $photos) { Remove-Item $photos -Recurse -Force }
Remove-Item (Join-Path $env:TEMP 'drdxt-visite-fin.txt') -ErrorAction SilentlyContinue

$p = Start-Process -FilePath $Exe -ArgumentList 'hors-ecran', 'scan-auto', 'deplier', "machine=$Machine", "--langue=$Langue" -PassThru
$racine = [System.Windows.Automation.AutomationElement]::RootElement
$condProc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)

$vus = @{}
$sansNom = @{}
$suspects = @{}
$debut = Get-Date
$motifCle = '^[A-Z][A-Za-z0-9]+(\.[A-Za-z0-9]+)+$'
$motifRecord = '^[A-Z][A-Za-z]+ \{ '
$motifFr = '\b(le|la|les|des|du|une|avec|pour|dans|est|pas)\b|[éèàçùêâô]'
$interactifs = 'button', 'check box', 'radio button', 'combo box', 'edit', 'hyperlink', 'list item', 'tab item', 'menu item', 'slider'

while (-not $p.HasExited -and ((Get-Date) - $debut).TotalSeconds -lt $DelaiMax) {
    Start-Sleep -Milliseconds 1500
    try {
        $fenetre = $racine.FindFirst([System.Windows.Automation.TreeScope]::Children, $condProc)
        if ($null -eq $fenetre) { continue }
        foreach ($e in $fenetre.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            try {
                $c = $e.Current
                $nom = $c.Name
                if ($interactifs -contains $c.LocalizedControlType -and $c.IsEnabled -and [string]::IsNullOrWhiteSpace($nom)) {
                    $sansNom["$($c.LocalizedControlType) | id=$($c.AutomationId) | classe=$($c.ClassName)"] = 1
                }
                if ([string]::IsNullOrWhiteSpace($nom) -or $vus.ContainsKey($nom)) { continue }
                $vus[$nom] = 1
                if ($nom -match $motifRecord) { $suspects["CHAMPS D'UN RECORD : $($nom.Substring(0, [Math]::Min(120, $nom.Length)))"] = 1; continue }
                if ($nom -match $motifCle) { $suspects["CLE NON TRADUITE : $nom"] = 1 }
                if ($nom -match '\{\d+(:[^}]*)?\}') { $suspects["GABARIT NON REMPLI : $nom"] = 1 }
                if ($nom -match '\bNaN\b|Infinity|System\.[A-Z]') { $suspects["VALEUR CASSEE : $nom"] = 1 }
                if ($Langue -like 'en*' -and $nom -match $motifFr) { $suspects["FRANCAIS EN ANGLAIS : $nom"] = 1 }
            } catch { }
        }
    } catch { }
}
$finVisite = Test-Path (Join-Path $env:TEMP 'drdxt-visite-fin.txt')
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $etat = 'arrete apres delai' } else { $etat = "fini, code $($p.ExitCode)" }

New-Item -ItemType Directory -Force -Path $Sortie | Out-Null
$rapport = @("machine=$Machine langue=$Langue : $etat ; visite complete=$finVisite ; $($vus.Count) textes lus")
$rapport += '--- controles interactifs sans nom accessible'
$rapport += $sansNom.Keys | Sort-Object
$rapport += '--- textes suspects'
$rapport += $suspects.Keys | Sort-Object
$rapport | Out-File -Encoding utf8 (Join-Path $Sortie "$Machine-$Langue.txt")
if (Test-Path $photos) { Copy-Item $photos (Join-Path $Sortie "$Machine-$Langue-photos") -Recurse -Force }
$rapport[0]
