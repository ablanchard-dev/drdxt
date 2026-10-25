#Requires -Version 5.1
<#
.SYNOPSIS
    Construit l'installeur DrDXT (DrDXT-Setup.exe), signe ou non.
    A lancer sur une machine de dev (SDK dotnet + Inno Setup 6 requis).
    L'installeur produit n'a besoin d'AUCUN runtime .NET sur la machine cible (self-contained win-x64).

.DESCRIPTION
    1. Valide la configuration de SIGNATURE — AVANT tout le reste.
    2. dotnet publish DrDxt.App --self-contained win-x64 -> installer/publish/
    3. Verifie que la charge utile est complete (App, Helper, PresentMonAPI2.dll).
    4. Copie PresentMonService.exe + NOTICE depuis third_party/presentmon/.
    5. Signe les executables de la charge utile (si -Sign).
    6. Compile DrDXT.iss avec ISCC -> installer/Output/.
    7. Signe l'installeur, puis VERIFIE la signature (si -Sign).

.NOTES
    ══ GATE G2 — POURQUOI CE SCRIPT EST ECRIT COMME CA ══

    Le premier ecran que voit un acheteur, c'est un avertissement SmartScreen sur un
    executable qui demande l'UAC et touche au GPU. La signature n'est donc pas un detail de
    packaging : c'est la premiere impression du produit.

    Le certificat n'existe pas encore (Certum cloud ~100 E/an, ou Azure Trusted Signing) —
    c'est une DECISION D'ACHAT, pas du code. Ce que le code peut faire, et fait ici :

    1. ON NE PEUT PAS EXPEDIER DU NON SIGNE PAR ACCIDENT. Sans -Sign, l'artefact
          s'appelle DrDXT-Setup-NON-SIGNE.exe. Un nom est la garde la moins chere qui
          survive a une soiree fatiguee : personne n'envoie « NON-SIGNE » a un client en
          croyant envoyer la version vendue.

    2. -Sign SANS CERTIFICAT ECHOUE, BRUYAMMENT ET TOUT DE SUITE. Jamais de repli
          silencieux sur du non signe : demander une signature et recevoir un binaire nu
          est exactement le genre de succes apparent que ce depot traque.

    3. UNE SIGNATURE EST VERIFIEE APRES COUP. signtool peut rendre 0 sur une chaine que
          Windows refusera ensuite. C'est la meme regle que les ecritures GPU du produit :
          une action relit avant d'affirmer.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # Vide = la version de Directory.Build.props, la seule source. Ne sert qu'a la surcharger.
    [string]$Version       = '',

    # Signe la charge utile et l'installeur. Exige un certificat : voir -CertThumbprint.
    [switch]$Sign,

    # Empreinte du certificat de signature de code (magasin CurrentUser\My).
    # A defaut, la variable d'environnement DRDXT_SIGN_THUMBPRINT est utilisee.
    [string]$CertThumbprint = $env:DRDXT_SIGN_THUMBPRINT,

    # Serveur d'horodatage. Sans horodatage, la signature expire AVEC le certificat et les
    # binaires deja vendus deviennent non signes du jour au lendemain.
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

$scriptDir  = $PSScriptRoot
$repoRoot   = Split-Path $scriptDir -Parent
$appProj    = Join-Path $repoRoot 'src\DrDxt.App\DrDxt.App.csproj'
$publishDir = Join-Path $scriptDir 'publish'
$outDir     = Join-Path $scriptDir 'Output'
$pmDir      = Join-Path $repoRoot 'third_party\presentmon'
$iss        = Join-Path $scriptDir 'DrDXT.iss'

# Une seule source de version, sinon l'installeur et l'App diraient deux numeros differents. Le numero
# choisi part au publish ET a l'installeur, jamais a un seul des deux.
if ([string]::IsNullOrWhiteSpace($Version)) {
    # Plusieurs PropertyGroup dans le fichier ; « .PropertyGroup.Version » rendait un tableau aplati en
    # « 0.9.0  » (espaces des groupes sans Version), et la ProductVersion livrée portait deux espaces.
    $Version = @(([xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup |
        ForEach-Object { $_.Version } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Aucune <Version> dans Directory.Build.props.' }
}

# Le nom DIT ce que le fichier est. Voir la note 1 ci-dessus.
$baseName = if ($Sign) { 'DrDXT-Setup' } else { 'DrDXT-Setup-NON-SIGNE' }

# --- 0. VALIDER LA SIGNATURE D'ABORD -----------------------------------------
# Avant le publish, qui prend des minutes : echouer vite sur une configuration absente vaut
# mieux qu'echouer tard sur une charge utile deja construite.
$signTool = $null
if ($Sign) {
    if ([string]::IsNullOrWhiteSpace($CertThumbprint)) {
        throw @'
-Sign demande mais AUCUN certificat fourni.

  Passez -CertThumbprint <empreinte>, ou definissez DRDXT_SIGN_THUMBPRINT.
  Aucun repli sur du non signe : demander une signature et recevoir un binaire nu est
  precisement ce que cette verification existe pour empecher.

  Le certificat reste a acheter (Certum cloud ~100 E/an, ou Azure Trusted Signing).
'@
    }

    # x64/x86 d'abord : un signtool ARM se trouve en premier dans le Windows Kit et ne
    # tourne pas ici.
    $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(x64|x86)\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signTool) { throw 'signtool.exe (x64/x86) introuvable — installez le Windows SDK.' }

    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $CertThumbprint }
    if (-not $cert) { throw "Certificat $CertThumbprint absent du magasin CurrentUser\My." }
    Write-Host "  [OK] certificat : $($cert.Subject)"
}

function Invoke-Sign {
    param([string]$Path)
    if (-not $Sign) { return }
    & $signTool sign /fd SHA256 /sha1 $CertThumbprint /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature echouee (exit $LASTEXITCODE) : $Path" }

    # ON RELIT. signtool peut rendre 0 sur une chaine que Windows refusera ensuite.
    & $signTool verify /pa /q $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature POSEE mais NON VERIFIEE (exit $LASTEXITCODE) : $Path" }
    Write-Host "  [SIGNE + VERIFIE] $(Split-Path $Path -Leaf)"
}

# ISCC.exe (compilateur Inno Setup) : PATH d'abord, puis l'installation utilisateur par defaut.
$isccCmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($isccCmd) { $isccCmd.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $iscc)) { throw "ISCC.exe (Inno Setup 6) introuvable. Installer via: winget install JRSoftware.InnoSetup" }

# --- 1. Publish self-contained ------------------------------------------------
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "Publishing $appProj ($Configuration, win-x64 self-contained)..."
dotnet publish "$appProj" -c $Configuration -r win-x64 --self-contained -o "$publishDir" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# --- 2. Verifier la charge utile ----------------------------------------------
# Le lanceur natif (NativeAOT, un seul fichier) reçoit l'élévation et redémarre le
# helper ou l'App avec un environnement reconstruit. Il faut les outils C++ de Visual Studio (link.exe).
# NativeAOT cherche vswhere.exe sur le PATH (cible Microsoft.NETCore.Native) : le dossier de l'installeur Visual Studio y passe.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$lanceurProj = Join-Path $repoRoot 'src\DrDxt.Lanceur\DrDxt.Lanceur.csproj'
$lanceurDir = Join-Path $scriptDir 'publish-lanceur'
if (Test-Path $lanceurDir) { Remove-Item $lanceurDir -Recurse -Force }
dotnet publish "$lanceurProj" -c $Configuration -r win-x64 -o "$lanceurDir" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (lanceur) failed (exit $LASTEXITCODE)" }
Copy-Item (Join-Path $lanceurDir 'DrDxt.Lanceur.exe') -Destination $publishDir -Force
Remove-Item $lanceurDir -Recurse -Force

foreach ($f in @('DrDxt.App.exe', 'DrDxt.ElevatedHelper.exe', 'DrDxt.Lanceur.exe', 'PresentMonAPI2.dll')) {
    if (Test-Path (Join-Path $publishDir $f)) { Write-Host "  [OK] $f" }
    else { throw "Payload incomplet : $f manquant dans $publishDir" }
}

# --- 3. Embarquer PresentMonService.exe (+ NOTICE) ----------------------------
# Il tourne en SYSTEM : chaque binaire vendore doit avoir l'empreinte complete notee dans VERSION.txt et la signature
# Authenticode valide d'Intel. Un binaire remplace (une PR, un disque abime) arrete la construction.
foreach ($ligne in Get-Content (Join-Path $pmDir 'VERSION.txt')) {
    if ($ligne -match '^\s+(\S+)\s+[\d ]+\s+sha256:([0-9A-F]{64})\s*$') {
        $nom, $attendue = $Matches[1], $Matches[2]
        $fichier = Join-Path $pmDir $nom
        $lue = (Get-FileHash $fichier -Algorithm SHA256).Hash
        if ($lue -ne $attendue) { throw "Empreinte differente de VERSION.txt : $nom ($lue)" }
        if ($nom -match '\.(exe|dll)$') {
            $sig = Get-AuthenticodeSignature $fichier
            if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Intel Corporation') {
                throw "Signature Intel absente ou invalide : $nom ($($sig.Status))"
            }
        }
        Write-Host "  [OK] $nom : empreinte et signature"
    }
}
Copy-Item (Join-Path $pmDir 'PresentMonService.exe') -Destination $publishDir -Force
Copy-Item (Join-Path $pmDir 'NOTICE-MIT.txt') -Destination (Join-Path $publishDir 'PresentMon-NOTICE-MIT.txt') -Force
Write-Host "  [OK] PresentMonService.exe + NOTICE"
# QRCoder (MIT), le QR du guide BIOS ; sa notice voyage avec la DLL.
Copy-Item (Join-Path $repoRoot 'third_party\qrcoder\LICENSE-MIT.txt') -Destination (Join-Path $publishDir 'QRCoder-LICENSE-MIT.txt') -Force
# L'avis de tous les composants tiers voyage avec l'application.
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination $publishDir -Force

# --- 3b. Le service de demarrage (case decochee de l'installeur) ------------------
# Publie a part, dans publish\service : c'est un autre programme (hote generique en service
# Windows), avec ses propres dependances ; les melanger a celles de l'App risquerait qu'une version
# ecrase l'autre. L'installeur ne l'enregistre que si la case est cochee.
$hostProj   = Join-Path $repoRoot 'src\DrDxt.ReconcilerHost\DrDxt.ReconcilerHost.csproj'
$serviceDir = Join-Path $publishDir 'service'
dotnet publish "$hostProj" -c $Configuration -r win-x64 --self-contained -o "$serviceDir" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (service) failed (exit $LASTEXITCODE)" }
if (Test-Path (Join-Path $serviceDir 'DrDxt.ReconcilerHost.exe')) { Write-Host "  [OK] service\DrDxt.ReconcilerHost.exe" }
else { throw "Payload incomplet : DrDxt.ReconcilerHost.exe manquant dans $serviceDir" }

# Aucun binaire livre ne porte le dossier du developpeur ni la racine du depot : l'editeur de liens natif y ecrivait le
# chemin complet du .pdb (DrDxt.Lanceur.exe). Lu en ASCII et en UTF-16.
$traces = @($env:USERPROFILE, $repoRoot) | Where-Object { $_ } | ForEach-Object { [regex]::Escape($_) }
foreach ($bin in Get-ChildItem $publishDir -Recurse -Include *.exe, *.dll) {
    $octets = [IO.File]::ReadAllBytes($bin.FullName)
    $textes = @([Text.Encoding]::ASCII.GetString($octets), [Text.Encoding]::Unicode.GetString($octets),
        [Text.Encoding]::Unicode.GetString($octets, 1, [Math]::Max(0, $octets.Length - 1)))
    foreach ($t in $traces) {
        if ($textes | Where-Object { $_ -match "(?i)$t" }) { throw "Chemin du developpeur dans $($bin.FullName)" }
    }
}
Write-Host "  [OK] aucun chemin du developpeur dans les binaires"

# --- 4. Signer la charge utile ------------------------------------------------
# Les DEUX executables maison, pas seulement l'installeur : le helper demande l'UAC de son
# cote, et un helper non signe derriere un installeur signe est le maillon que SmartScreen
# et un antivirus regardent.
if ($Sign) {
    foreach ($exe in @('DrDxt.App.exe', 'DrDxt.ElevatedHelper.exe', 'DrDxt.Lanceur.exe', 'service\DrDxt.ReconcilerHost.exe')) {
        Invoke-Sign (Join-Path $publishDir $exe)
    }
}

# --- 5. Compiler l'installeur -------------------------------------------------
if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
Write-Host "Compiling installer with $iscc ..."
& $iscc "/DAppVer=$Version" "/DOutBase=$baseName" "$iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

# --- 6. Verifier que l'installeur existe, puis le signer ----------------------
$setup = Join-Path $outDir "$baseName.exe"
if (-not (Test-Path $setup)) { throw "Installeur non produit : $setup" }
Invoke-Sign $setup

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "Installeur pret : $setup  ($sizeMb Mo)"
if ($Sign) {
    Write-Host 'SIGNE et VERIFIE.'
} else {
    Write-Host 'NON SIGNE — le nom du fichier le dit. A NE PAS envoyer a un client :'
    Write-Host 'il verra un avertissement SmartScreen sur un executable qui demande l''UAC.'
}
