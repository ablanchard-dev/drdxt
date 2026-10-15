<#
.SYNOPSIS
  Garde de mutation : une mutation ne peut pas survivre dans un commit.

.DESCRIPTION
  Deux fois dans la nuit du 11/09/2026, un etat mute a survecu dans l'arbre : un script de mutation
  importe au lieu d'etre execute (la restauration n'a jamais tourne), puis une restauration faite
  depuis la MAUVAISE copie. Les deux ont ete rattrapes a la main par `git diff`. Ce script remplace
  la vigilance par une garde.

    sauver       <fichier>            copie horodatee + empreinte SHA-256 AVANT de muter
    restaurer    <fichier>            remet la copie, verifie l'empreinte, touche la date
    verifier     <fichier>            le fichier est-il identique a la copie d'avant ?
    avant-commit <fichier> [...]      refuse si un fichier hors liste est modifie, ou si une
                                      mutation n'a pas ete restauree

  Les copies et le registre vivent dans `.git/garde-mutation/` : jamais suivis, jamais balayes par
  un `git add`, et rien n'y est jamais efface. Code de sortie 0 = accepte, 1 = refuse.

  `avant-commit` compare a la liste du lot les fichiers SUIVIS modifies (`git diff HEAD`) ET les
  fichiers NON SUIVIS non ignores (`git ls-files --others --exclude-standard`, depuis le 11/09/2026 :
  avant, un fichier neuf pas encore indexe etait annonce « n'a pas bouge », et un fichier egare
  n'etait jamais refuse). Quelques chemins non suivis sont TOLERES hors de tout lot (`$Toleres`) :
  un par un, chemin exact ET empreinte SHA-256. Un fichier tolere dont le contenu change est
  refuse, et un fichier neuf a cote de lui aussi : la tolerance ne couvre que ce qui existait.

  CE QUE LA GARDE NE GARANTIT PAS :
    * elle verifie l'INTEGRITE d'une restauration (octet pour octet), pas la JUSTESSE du code
      restaure : un code faux avant la mutation reste faux apres, avec la bonne empreinte ;
    * elle ne prouve pas qu'une mutation a ete TESTEE, ni qu'elle a fait rougir quoi que ce soit ;
    * elle ne voit que ce qu'on lui demande : une mutation faite sans `sauver` n'est pas dans son
      registre, et seule la comparaison a la liste du lot peut encore l'attraper ;
    * elle ne lit ni les fichiers ignores par git, ni rien hors du depot.

.EXAMPLE
  powershell -File tools/garde-mutation.ps1 sauver src/DrDxt.Core/Autopilot/KeepPolicy.cs
  powershell -File tools/garde-mutation.ps1 restaurer src/DrDxt.Core/Autopilot/KeepPolicy.cs
  powershell -File tools/garde-mutation.ps1 avant-commit src/a.cs tests/b.cs docs/NON-NUISANCE.md
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('sauver', 'restaurer', 'verifier', 'avant-commit')]
    [string]$Action,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Fichiers
)

$ErrorActionPreference = 'Stop'

function Refuser([string]$message) {
    Write-Host "REFUS : $message"
    exit 1
}

# Sous PS 5.1, le stderr d'un exe natif (les avertissements LF/CRLF de git) peut devenir une erreur
# terminale avec `Stop` : les appels git passent donc en `Continue`, stderr jete.
function Git-Sortie { $ErrorActionPreference = 'Continue'; & git @args 2>$null }

$racine = (Git-Sortie rev-parse --show-toplevel)
if (-not $racine) { Refuser "pas dans un depot git" }
$racine = $racine.Trim() -replace '/', '\'
$registre = Join-Path $racine '.git\garde-mutation'
if (-not (Test-Path -LiteralPath $registre)) { New-Item -ItemType Directory -Path $registre | Out-Null }

function Relatif([string]$f) {
    $plein = if ([IO.Path]::IsPathRooted($f)) { [IO.Path]::GetFullPath($f) }
             else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $f)) }
    if (-not $plein.StartsWith($racine + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Refuser "$f est hors du depot"
    }
    return $plein.Substring($racine.Length).TrimStart('\') -replace '\\', '/'
}

function Empreinte([string]$chemin) { (Get-FileHash -LiteralPath $chemin -Algorithm SHA256).Hash }

# Les chemins NON SUIVIS toleres hors de tout lot : un par un, jamais un joker, avec l'empreinte
# du contenu tolere. `audits/` est non suivi et n'appartient a aucun lot.
# Le suivre ou le changer est une decision : l'empreinte l'impose, ce fichier doit bouger avec lui.
$Toleres = @{
    'audits/FABLE-5-1-AUDIT-2026-09-10.md' = '537C24C305ABC9BF09679913FBC96F6D52BD167D2DD6D490C70C2D455CC1B8C2'
    'audits/MISSION-FABLE-5-1.md'          = '0F83E78ED45A71F24E80A9A91EB6492B985925A30C0A402E9095D0904E8B9A13'
    'audits/PLAN-FABLE-5-1-2026-09-10.md'  = '6DF85FF35EA70E6E7CC2949595B14272EEB7393679B86D501E294D02C149DA1C'
}

# Un enregistrement EN COURS par fichier : `<cle>.en-cours` (empreinte, copie). Il devient
# `<cle>.<horodatage>.restaure` quand la restauration est prouvee.
function Cle([string]$rel) { $rel -replace '[/\\:]', '__' }
function EnCours([string]$rel) { Join-Path $registre ((Cle $rel) + '.en-cours') }

function Lire-EnCours([string]$rel) {
    $f = EnCours $rel
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    $l = (Get-Content -LiteralPath $f -Raw).Trim() -split "`t"
    return @{ Empreinte = $l[0]; Copie = $l[1]; Chemin = $f }
}

function Verifier-Contre([string]$rel, $enr) {
    $cible = Join-Path $racine $rel
    if (-not (Test-Path -LiteralPath $cible)) { Refuser "$rel n'existe plus" }
    $vu = Empreinte $cible
    if ($vu -ne $enr.Empreinte) {
        Refuser ("$rel N'EST PAS l'etat d'avant la mutation.`n" +
                 "  attendu : $($enr.Empreinte)`n  trouve  : $vu`n" +
                 "  la bonne copie : $($enr.Copie)")
    }
}

switch ($Action) {
    'sauver' {
        if ($Fichiers.Count -ne 1) { Refuser "sauver prend exactement un fichier" }
        $rel = Relatif $Fichiers[0]
        $cible = Join-Path $racine $rel
        if (-not (Test-Path -LiteralPath $cible)) { Refuser "$rel introuvable" }
        # Une 2e sauvegarde sur un fichier deja en cours photographierait l'etat MUTE : c'est
        # exactement comme ca qu'on finit par restaurer la mauvaise copie.
        if (Lire-EnCours $rel) { Refuser "$rel a deja une mutation en cours : restaure-la d'abord" }
        $horo = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $copie = Join-Path $registre ((Cle $rel) + ".$horo.copie")
        Copy-Item -LiteralPath $cible -Destination $copie
        $h = Empreinte $cible
        if ((Empreinte $copie) -ne $h) { Refuser "la copie de $rel differe de l'original" }
        Set-Content -LiteralPath (EnCours $rel) -Value "$h`t$copie" -Encoding ASCII
        Write-Host "SAUVE : $rel  sha256=$h"
        Write-Host "        copie : $copie"
    }
    'restaurer' {
        if ($Fichiers.Count -ne 1) { Refuser "restaurer prend exactement un fichier" }
        $rel = Relatif $Fichiers[0]
        $enr = Lire-EnCours $rel
        if (-not $enr) { Refuser "$rel n'a aucune mutation en cours" }
        if ((Empreinte $enr.Copie) -ne $enr.Empreinte) { Refuser "la copie $($enr.Copie) a ete alteree" }
        $cible = Join-Path $racine $rel
        Copy-Item -LiteralPath $enr.Copie -Destination $cible -Force
        Verifier-Contre $rel $enr
        # La date touchee : sans elle, un build incremental peut garder l'assembly MUTEE.
        (Get-Item -LiteralPath $cible).LastWriteTime = Get-Date
        Rename-Item -LiteralPath $enr.Chemin -NewName ((Cle $rel) + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.restaure')
        Write-Host "RESTAURE : $rel  sha256=$($enr.Empreinte)  (date touchee)"
    }
    'verifier' {
        if ($Fichiers.Count -ne 1) { Refuser "verifier prend exactement un fichier" }
        $rel = Relatif $Fichiers[0]
        $enr = Lire-EnCours $rel
        if (-not $enr) { Refuser "$rel n'a aucune mutation en cours" }
        Verifier-Contre $rel $enr
        Write-Host "IDENTIQUE : $rel a l'empreinte d'avant la mutation"
    }
    'avant-commit' {
        if (-not $Fichiers -or $Fichiers.Count -eq 0) { Refuser "donne la liste des fichiers que le lot a le droit de toucher" }
        $autorises = @($Fichiers | ForEach-Object { Relatif $_ })

        $enCours = @(Get-ChildItem -LiteralPath $registre -Filter '*.en-cours' -ErrorAction SilentlyContinue)
        if ($enCours.Count -gt 0) {
            Refuser ("mutation(s) jamais restauree(s) :`n  " +
                     (($enCours | ForEach-Object { $_.BaseName -replace '__', '/' }) -join "`n  "))
        }

        Push-Location $racine
        try {
            # HEAD couvre l'indexe ET le non indexe : un fichier stage puis mute se voit aussi.
            # quotePath=false : un chemin accentue sort tel quel, pas en octets echappes.
            $modifies = @(@(Git-Sortie -c core.quotePath=false diff --name-only HEAD) | Where-Object { $_ })
            $nonSuivis = @(@(Git-Sortie -c core.quotePath=false ls-files --others --exclude-standard) | Where-Object { $_ })
        } finally { Pop-Location }

        foreach ($n in $nonSuivis) {
            if ($autorises -notcontains $n -and $Toleres.ContainsKey($n)) {
                $vu = Empreinte (Join-Path $racine $n)
                if ($vu -ne $Toleres[$n]) {
                    Refuser ("$n est tolere tel quel, et son contenu a change.`n" +
                             "  attendu : $($Toleres[$n])`n  trouve  : $vu")
                }
                Write-Host "tolere : $n (non suivi, empreinte identique)"
                continue
            }
            $modifies += $n
        }

        $horsListe = @($modifies | Where-Object { $autorises -notcontains $_ })
        if ($horsListe.Count -gt 0) {
            Refuser ("fichier(s) modifie(s) HORS de la liste du lot :`n  " + ($horsListe -join "`n  "))
        }
        $intacts = @($autorises | Where-Object { $modifies -notcontains $_ })
        foreach ($i in $intacts) { Write-Host "note : $i est dans la liste mais n'a pas bouge" }
        Write-Host "ACCEPTE : $($modifies.Count) fichier(s) modifie(s), tous dans la liste du lot, aucune mutation en cours"
    }
}
exit 0
