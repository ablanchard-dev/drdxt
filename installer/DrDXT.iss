; ============================================================================
;  DrDXT - Installeur Windows (Inno Setup 6)
;
;  Build TESTEUR, NON SIGNE (phase de validation avant signature/structure).
;  SmartScreen = "Executer quand meme" ; Smart App Control = OFF par defaut sauf
;  install neuve Win11 (cf README-testeur.txt).
;
;  Payload = installer\publish\ (produit par build-installer.ps1 :
;    dotnet publish DrDxt.App --self-contained win-x64  ->  App + ElevatedHelper +
;    DLLs PresentMon + runtime .NET embarque, PUIS copie de PresentMonService.exe).
;  Le testeur n'a donc AUCUN runtime a installer.
;
;  COMPILER : build-installer.ps1 appelle ISCC.exe avec /DAppVer=<version> et
;    /DOutBase=<nom>. Sans -Sign, le nom porte NON-SIGNE : personne n'envoie
;    « NON-SIGNE » a un joueur en croyant envoyer la version signee.
;  SIGNER : build-installer.ps1 -Sign -CertThumbprint <empreinte>. Le script signe
;    l'App, le Helper ET l'installeur, puis VERIFIE chaque signature -- signtool peut
;    rendre 0 sur une chaine que Windows refusera ensuite. Il refuse net si le
;    certificat manque, sans jamais se rabattre sur du non signe.
;    Le certificat reste a acheter (Certum cloud ~100E/an, ou Azure Trusted Signing).
; ============================================================================

#ifndef AppVer
  #error AppVer manquant : lancer build-installer.ps1, qui lit la version dans Directory.Build.props.
#endif
#define AppName       "DrDXT"
#define AppPublisher  "DrDXT"
#define AppExe        "DrDxt.App.exe"
#define PmService     "PresentMonService"
; Doit rester identique a PresentMonNaming.ServiceArguments (src/DrDxt.Core) : un test
; d'integration compare les deux, parce que le service et le client sont deux processus
; qui n'ont que cette ligne en commun.
;
; VIDE, ET C'EST UNE MESURE, PAS UN OUBLI. `--control-pipe \\.\pipe\drdxt-presentmon-control`
; donnerait a DrDXT un tube prive pour cohabiter avec le service Intel, mais trois mesures par le
; SCM le refutent : le serveur accepte l'option et publie quand meme le nom par defaut,
; en 2.4.1 comme avec le binaire Intel 2.5.1. Voir docs/PRESENTMON-COEXISTENCE.md.
;
; (Au passage, mesure avec ISCC : le preprocesseur d'Inno consomme un antislash sur deux
; dans les chaines, guillemets simples ET doubles. Tout argument futur contenant un chemin
; devra doubler ses antislash, et le test le verifie.)
#define PmServiceArgs ""
; Le service qui reapplique les reglages au demarrage. Doit rester identique au ServiceName de
; l'hote, src/DrDxt.ReconcilerHost/Program.cs (test d'integration).
#define RcService "DrDxtReconciler"
; L'empreinte SHA-256 de chaque executable lance en SYSTEM ou eleve (et de la DLL qu'il charge), calculee ici a la
; compilation, apres la signature de build-installer.ps1 ; relue sur le disque avant sc start et schtasks (EmpreintesConformes).
; Couche 2 = detection ; la protection est le verrou du dossier (PreparerLeDossier) : les autres DLL du dossier
; (DrDxt.Core, le reconciliateur...) ne sont pas empreintees, le verrou les rend deja non remplacables.
#define EmpreinteHote GetSHA256OfFile(AddBackslash(SourcePath) + "publish\service\DrDxt.ReconcilerHost.exe")
#define EmpreinteHoteDll GetSHA256OfFile(AddBackslash(SourcePath) + "publish\service\DrDxt.ReconcilerHost.dll")
#define EmpreintePresentMon GetSHA256OfFile(AddBackslash(SourcePath) + "publish\PresentMonService.exe")
#define EmpreinteHelper GetSHA256OfFile(AddBackslash(SourcePath) + "publish\DrDxt.ElevatedHelper.exe")
#define EmpreinteHelperDll GetSHA256OfFile(AddBackslash(SourcePath) + "publish\DrDxt.ElevatedHelper.dll")
#define EmpreinteLanceur GetSHA256OfFile(AddBackslash(SourcePath) + "publish\DrDxt.Lanceur.exe")
#define EmpreinteApp GetSHA256OfFile(AddBackslash(SourcePath) + "publish\DrDxt.App.exe")
#define EmpreinteAppDll GetSHA256OfFile(AddBackslash(SourcePath) + "publish\DrDxt.App.dll")

[Setup]
AppId={{B7E4D2A1-9C3F-4E56-8A0D-1F2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVer}
DefaultDirName={autopf}\DrDXT
; Le dossier ne se choisit pas. Hors de Program Files (C:\DrDXT hérite
; des droits de C:\), un compte sans droits admin pouvait remplacer le helper, lancé élevé à la demande
; et par la désinstallation. UsePreviousAppDir=no : une mise à jour ne reprend pas un ancien dossier choisi.
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=DrDXT
DisableProgramGroupPage=yes
AllowNoIcons=yes
; La licence GPL v3 se lit et s'accepte dans l'installeur.
LicenseFile=..\LICENSE
OutputDir=Output
; Le nom DIT ce que le fichier est : build-installer.ps1 passe DrDXT-Setup-NON-SIGNE
; tant qu'aucun certificat n'est utilise.
#ifndef OutBase
  #define OutBase "DrDXT-Setup-NON-SIGNE"
#endif
OutputBaseFilename={#OutBase}
; Le service embarque son propre runtime .NET, 223 fichiers identiques a ceux de l'App
; (87 Mo). Avec le dictionnaire par defaut, trop court pour voir les deux copies, l'installeur pesait
; 73,1 Mo ; avec un dictionnaire plus grand que le contenu (238 Mo), la seconde copie ne coute presque
; rien : 45,1 Mo mesures. Prix : l'installation demande autant de memoire que le dictionnaire.
Compression=lzma2/ultra64
LZMADictionarySize=262144
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
; L'anglais, et le francais quand Windows est en francais : la langue de l'interface choisit, sans question.
ShowLanguageDialog=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayName=DrDXT
UninstallDisplayIcon={app}\{#AppExe}
SetupLogging=yes
; --- SIGNATURE ---
; L'installeur lui-meme est signe APRES compilation par build-installer.ps1 (qui verifie
; ensuite la signature). Le desinstalleur, lui, est genere par Inno et ne peut etre signe
; qu'ici : SignedUninstaller exige un SignTool declare dans la configuration d'Inno Setup.
; A activer le jour du certificat, avec la ligne SignTool correspondante :
;   SignTool=drdxt_sign $f
;   SignedUninstaller=yes

[Languages]
; La premiere langue sert quand celle de Windows n'est pas proposee.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
; Chaque texte que le joueur lit, dans les deux langues (LInstalleurParleAnglaisEtFrancaisTests).
en.TacheBureau=Create a desktop shortcut
fr.TacheBureau=Créer un raccourci sur le Bureau
en.GroupeRaccourcis=Shortcuts:
fr.GroupeRaccourcis=Raccourcis :
en.TacheService=Reapply my settings when Windows starts and read the processor temperature during my games (DrDxtReconciler service)
fr.TacheService=Réappliquer mes réglages au démarrage de Windows et relever la température du processeur pendant mes parties (service DrDxtReconciler)
en.TacheHelper=Run Optimize trials without opening DrDXT (starts the administrator helper when you sign in)
fr.TacheHelper=Faire les essais d'Optimiser sans ouvrir DrDXT (lance l'assistant administrateur à l'ouverture de session)
en.GroupeDemarrage=Startup:
fr.GroupeDemarrage=Démarrage :
en.EtatServiceMesure=Registering the measurement service...
fr.EtatServiceMesure=Enregistrement du service de mesure...
en.EtatServiceDemarrage=Registering the startup service...
fr.EtatServiceDemarrage=Enregistrement du service de démarrage...
en.HorsProgramFiles=DrDXT only installs in %1: anywhere else, a program without administrator rights could replace its services' files. Run the installer again without the /DIR option.
fr.HorsProgramFiles=DrDXT s'installe seulement dans %1 : ailleurs, un programme sans droits administrateur pourrait remplacer les fichiers de ses services. Relance l'installation sans l'option /DIR.
en.ProtectionEchouee=Protecting DrDXT's folder failed, or one of its files differs from the installer's: the startup services and the match recording are not installed, so that no program of your account can replace their files. DrDXT works without them; run the installer again to retry.
en.ReleveNonPose=The match recording could not be registered with Windows: your games are not recorded until the installer is run again. DrDXT works without it.
en.ReleveCeCompte=Windows refused the match recording for every account of this PC: it is set up for the account that approved the installation, and for that account only.
fr.ReleveCeCompte=Windows a refusé le relevé des parties pour tous les comptes de ce PC : il est posé pour le compte qui a validé l'installation, et pour lui seul.
fr.ReleveNonPose=Le relevé des parties n'a pas pu être enregistré auprès de Windows : tes parties ne sont pas relevées tant que l'installation n'est pas relancée. DrDXT fonctionne sans lui.
fr.ProtectionEchouee=La protection du dossier de DrDXT a échoué, ou l'un de ses fichiers diffère de celui de l'installeur : les services de démarrage et le relevé des parties ne sont pas installés, pour qu'aucun programme de ton compte ne puisse remplacer leurs fichiers. DrDXT fonctionne sans eux ; relance l'installation pour réessayer.
en.VerrouImpossible=DrDXT's folder could not be locked before copying its files: nothing is installed, so that no program of your account can replace them while they are copied. Run the installer again.
en.AncienDossierReste=The previous DrDXT installation, in %1, is still on the disk: nothing points to it any more. You can delete it in File Explorer (Windows may ask for administrator approval).
fr.AncienDossierReste=L'ancienne installation de DrDXT, dans %1, reste sur le disque : plus rien n'y pointe. Tu peux la supprimer dans l'Explorateur de fichiers (Windows peut demander l'accord administrateur).
en.DonneesImpossibles=DrDXT's data folder (ProgramData\DrDxt) could not be locked: nothing is installed, so that no program of your account can plant what the startup service trusts. Run the installer again.
fr.DonneesImpossibles=Le dossier des données de DrDXT (ProgramData\DrDxt) n'a pas pu être verrouillé : rien n'est installé, pour qu'aucun programme de ton compte ne puisse y poser ce que le service de démarrage croit. Relance l'installation.
fr.VerrouImpossible=Le dossier de DrDXT n'a pas pu être verrouillé avant la copie de ses fichiers : rien n'est installé, pour qu'aucun programme de ton compte ne puisse les remplacer pendant la copie. Relance l'installation.
en.SystemTempAbsent=Windows is missing a folder that its updates add (Windows\SystemTemp), which DrDXT needs to install safely. Run Windows Update, restart, then run the installer again.
fr.SystemTempAbsent=Il manque à Windows un dossier que ses mises à jour ajoutent (Windows\SystemTemp), dont DrDXT a besoin pour s'installer en sécurité. Lance Windows Update, redémarre, puis relance l'installation.
en.RenduIncomplet=DrDXT could not undo everything it had changed. What is left is listed in the desinstallation-<date>.txt report in the %1 folder. That folder stays so it can be retried: reinstall DrDXT, then uninstall again, or open that report.
fr.RenduIncomplet=DrDXT n'a pas pu tout remettre comme avant. Ce qui reste est écrit dans le rapport desinstallation-<date>.txt du dossier %1. Ce dossier reste en place pour réessayer : réinstalle DrDXT puis désinstalle à nouveau, ou ouvre ce rapport.

[Tasks]
Name: "desktopicon"; Description: "{cm:TacheBureau}"; GroupDescription: "{cm:GroupeRaccourcis}"
; Cochee par defaut, et visible : le joueur la decoche s'il n'en veut pas. Le releve des parties tourne sans droits
; administrateur et lit la temperature du processeur par ce service ; sans lui, elle n'est plus relevee en partie, et les
; reglages gardes ne sont plus reverifies au demarrage. Une mise a jour garde le choix de l'installation precedente.
Name: "reconciler"; Description: "{cm:TacheService}"; GroupDescription: "{cm:GroupeDemarrage}"
; Decochee par defaut : un processus administrateur pour toute la session est une surface que chaque joueur choisit. Une
; mise a jour garde le choix de l'installation precedente ; decochee, la tache existante part (PoserOuRetirerLeHelper).
; Proposee seulement au compte d'origine administrateur : HighestAvailable n'eleve pas un compte standard.
Name: "helperseance"; Description: "{cm:TacheHelper}"; GroupDescription: "{cm:GroupeDemarrage}"; Flags: unchecked; Check: CompteDOrigineAdmin

[InstallDelete]
; Le raccourci « Desinstaller DrDXT » prend ses accents ; sans cette ligne, une mise a jour
; laisserait l'ancien a cote du nouveau dans le menu Demarrer.
Type: files; Name: "{group}\Desinstaller DrDXT.lnk"
; Le raccourci de desinstallation porte le nom de la langue de l'installation : celui de l'autre langue part.
Type: files; Name: "{group}\Désinstaller DrDXT.lnk"; Languages: en
Type: files; Name: "{group}\Uninstall DrDXT.lnk"; Languages: fr
; Celui d'une version precedente lancait l'App de son dossier, peut-etre hors Program Files (la 0.9.1 acceptait /DIR) : il
; part, et revient seulement si la case est cochee.
Type: files; Name: "{autodesktop}\DrDXT.lnk"
; Les fichiers de symboles ne sont plus livres, mais une mise a jour n'efface que ce qu'elle remplace :
; sans ce retrait, des .pdb d'une ancienne version resteraient a cote des nouveaux binaires.
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\service\*.pdb"
; Les copies de repli de PresentMon ne sont plus livrees (voir [Files]) ; une mise a jour n'efface que ce qu'elle remplace :
; sans ces lignes, celles d'une ancienne version resteraient, jamais lues.
Type: filesandordirs; Name: "{app}\third_party"
Type: filesandordirs; Name: "{app}\service\third_party"

[UninstallDelete]
; Le staging de la mise a jour (%LOCALAPPDATA%\DrDXT\staging) ne se retire pas ici : le desinstalleur est administrateur, et
; le dossier appartient au compte, qui peut en faire une jonction vers un dossier qu'il ne peut pas vider lui-meme (la
; RedirectionGuard d'Inno ne la bloque qu'a partir de Windows 10 22H2). L'App, sans droits, le purge a son lancement.
; Les temoins d'incident ecrits par le service partent avec lui, par le code (SupprimerLesTemoins) : un dossier vide a
; l'aveugle suivrait une jonction posee a sa place.
; Ce que le helper a ecrit pour le service part avec lui : une pause du maintien laissee survivrait a une
; reinstallation. Le plafond lui-meme est deja rendu (service arrete au desinstallement, puis RemoveUploadCapOp
; dans [UninstallRun]). Le fichier exact, jamais le dossier : la cle et le manifeste de la machine y vivent.
Type: files; Name: "{commonappdata}\DrDxt\pause-du-maintien"
; La trace du compteur DXGI d'une App elevee tuee en pleine mesure (jusqu'a 1 Go) : sa session est arretee plus haut
; (CurUninstallStepChanged), et seule une mesure suivante rangeait le fichier. Deux dossiers reserves aux administrateurs
; seulement (le TEMP que le lanceur donne aux processus eleves), jamais Windows\Temp, ou les comptes creent.
Type: files; Name: "{win}\SystemTemp\drdxt-presents-*.etl"
Type: files; Name: "{commonappdata}\DrDxt\temp\drdxt-presents-*.etl"
Type: files; Name: "{commonappdata}\DrDxt\plafond-envoi.json"
; Un dossier "%ProgramData%" litteral, depose par un composant de Windows quand l'App elevee
; tournait sans cette variable, bloquait la suppression du dossier ("Failed to delete directory
; (145)"). Seul le fichier exact part, puis chaque dossier parent s'il est vide, dans l'ordre (Inno
; traite cette section dans l'ordre du script). Jamais filesandordirs sous {app} : il suivrait une
; jonction posee dans le residu, et {app} peut etre un dossier du joueur (installation d'avant la
; page de choix fermee, ou d'une version qui acceptait encore /DIR= hors de Program Files).
Type: files; Name: "{app}\%ProgramData%\Microsoft\Windows\UUS\State\_active.uusver"
Type: dirifempty; Name: "{app}\%ProgramData%\Microsoft\Windows\UUS\State"
Type: dirifempty; Name: "{app}\%ProgramData%\Microsoft\Windows\UUS"
Type: dirifempty; Name: "{app}\%ProgramData%\Microsoft\Windows"
Type: dirifempty; Name: "{app}\%ProgramData%\Microsoft"
Type: dirifempty; Name: "{app}\%ProgramData%"
Type: dirifempty; Name: "{app}"

[Registry]
; Le helper y retient la limite de puissance du firmware a ce demarrage (garde Vmin) : partie avec DrDXT.
Root: HKLM; Subkey: "Software\DrDXT"; Flags: uninsdeletekey dontcreatekey

[Run]
; La tache planifiee du releve (« DrDXT releve ») est posee par CurStepChanged, dans la branche ou le dossier est
; protege : une tache au niveau le plus eleve qui lancerait un exe remplacable par le compte serait une elevation sans invite.

[Files]
; Payload complet (App + helper + runtime + DLLs PresentMon + PresentMonService.exe + NOTICE)
; Excludes "*.pdb" : mesure du payload construit, 13 fichiers de symboles, 576 Ko.
; Un .pdb ne porte pas que des numeros de ligne : lu octet par octet, celui du helper eleve contient
; 57 noms de fichiers .cs et le chemin de l'arborescence de build jusqu'au dossier personnel de son
; auteur. Sur un produit ferme dont le moat est l'allowlist default-DENY du helper, c'est le plan du
; coffre livre avec le coffre. Les symboles restent PRODUITS (on ne passe pas DebugType=none) : ils
; servent a lire une pile d'appel cote build. C'est leur livraison qui est refusee, pas leur existence.
; Excludes "\third_party" : mesure du payload construit, PresentMonService.exe y est DEUX
; fois, 3,6 Mo chacun, empreinte identique. La racine vient de l'etape 3 de build-installer.ps1 (c'est
; elle que [Code] enregistre) ; celle sous third_party\presentmon\ vient du csproj du helper et sert de
; repli EN DEVELOPPEMENT. Or le locator essaie la racine d'abord, et l'installeur la garantit : chez le
; joueur cette seconde copie n'est jamais lue. On poserait 3,6 Mo d'un binaire de service que
; l'installeur n'enregistre pas. Le repli reste PRODUIT cote build, c'est sa livraison qui est refusee.
; Excludes "\service\third_party" : la meme copie de repli, posee par le publish du service. Elle n'est jamais lue : la seule
; op qui cherche ce binaire (InstallPresentMonServiceOp) n'est emise que par SelfHealingBenchmarkRunner, que seule l'App
; enregistre (AddDrDxtMeasurement) ; elle part au helper lance depuis {app}, qui prend {app}\PresentMonService.exe ; le
; service n'a ni banc ni mesure, et le service PresentMon s'enregistre depuis {app} (InstallPresentMonService plus bas).
; Garde : InstallateurPresentMonTests.
Source: "publish\*";           DestDir: "{app}"; Excludes: "*.pdb,\third_party,\service\third_party"; Flags: ignoreversion recursesubdirs createallsubdirs
; Le lanceur sert AVANT la copie (preparation des dossiers de confiance) : copie extractible tot, verifiee par empreinte.
Source: "publish\DrDxt.Lanceur.exe"; Flags: dontcopy
Source: "README-testeur.txt";  DestDir: "{app}"; Flags: ignoreversion isreadme; Languages: fr
Source: "README-tester.txt";   DestDir: "{app}"; Flags: ignoreversion isreadme; Languages: en

[Icons]
Name: "{group}\DrDXT";            Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,DrDXT}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DrDXT";      Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[UninstallRun]
; RENDRE LES REGLAGES avant de retirer les fichiers. Ordre REEL de la desinstallation,
; lu dans l'aide d'Inno 6 (« [Run] & [UninstallRun] sections », « [UninstallDelete] section »,
; « Installation Order ») et dans sa source (Projects/Src/Setup.Uninstall.pas) :
;   1. CurUninstallStepChanged(usUninstall) : le [Code] ci-dessous tue l'App et le helper, arrete et
;      retire le service PresentMon. Plus rien ne peut reappliquer pendant le rendu.
;   2. UninstLog.PerformUninstall : [UninstallRun] EN PREMIER (« as the first step of uninstallation »),
;      puis les fichiers installes (journal relu de la fin au debut), puis [UninstallDelete] EN DERNIER.
;   3. CurUninstallStepChanged(usPostUninstall), puis usDone.
; Le helper est donc encore sur le disque quand cette ligne tourne. Il herite des droits du
; desinstalleur (PrivilegesRequired=admin ; runascurrentuser est le defaut hors postinstall) : aucun
; tube, aucune UAC. NON BLOQUANT : l'aide ne decrit aucun controle du code de retour d'une entree
; [UninstallRun] ; le mode borne lui-meme sa duree (120 s), rend 0 (2 s'il n'est pas eleve), et ecrit son rapport sous
; %ProgramData%\DrDxt\desinstallation-<heure UTC>.txt. skipifdoesntexist : un helper absent n'ajoute
; pas d'avertissement. Un test d'integration relit cette ligne.
; PAR LE LANCEUR NATIF, comme tout lancement eleve : lance directement, le helper .NET
; heriterait de HKCU\Environment (CORECLR_PROFILER... chargerait une DLL du compte en administrateur). Le lanceur reconstruit
; l'environnement et attend la fin du helper dans ce mode.
Filename: "{app}\DrDxt.Lanceur.exe"; Parameters: "DrDxt.ElevatedHelper.exe --uninstall-revert"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DrDxtRendreLesReglages"
[Code]
{ --- Les outils Windows se lancent par la constante sys d'Inno (System32 64 bits, installeur en
      mode 64 bits), jamais par leur nom seul. Il tourne en administrateur ; un nom seul laisse
      Windows chercher d'abord ailleurs, dont le dossier courant (souvent Telechargements).
      Meme regle que le helper. Un test d'integration refuse un Exec sans chemin. --- }

{ --- L'environnement de l'installeur, avant tout outil lance. Eleve par l'UAC, il recoit celui de HKCU\Environment,
      que tout programme du compte ecrit sans droits ; ses enfants en heritent. Un outil eleve (icacls, sc, le lanceur
      natif, et a la desinstallation le helper lance par le lanceur) y lirait un windir ou un PATH du compte qui le
      menerait ailleurs. Comme DrDxt.Lanceur pour le helper, rien de l'environnement recu ne reste : chaque variable est
      retiree, puis le minimum est repose depuis les constantes d'Inno (lues aux API, jamais a l'environnement). TEMP :
      SystemTemp (SYSTEM et Administrateurs seuls), sinon Windows\Temp ; jamais le Temp du compte. --- }
function GetEnvironmentStringsW(): Longword; external 'GetEnvironmentStringsW@kernel32.dll stdcall';
function FreeEnvironmentStringsW(Bloc: Longword): Boolean; external 'FreeEnvironmentStringsW@kernel32.dll stdcall';
function lstrlenW(Texte: Longword): Integer; external 'lstrlenW@kernel32.dll stdcall';
function lstrcpynW(Cible: String; Source: Longword; Longueur: Integer): Longword; external 'lstrcpynW@kernel32.dll stdcall';
function SetEnvironmentVariableW(Nom: String; Valeur: String): Boolean; external 'SetEnvironmentVariableW@kernel32.dll stdcall';
function EffacerVariable(Nom: String; Rien: Longword): Boolean; external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function NomsDeLEnvironnement(): TArrayOfString;
var
  Bloc, Courant: Longword;
  Longueur, Egal: Integer;
  Entree: String;
begin
  SetArrayLength(Result, 0);
  Bloc := GetEnvironmentStringsW();
  if Bloc = 0 then Exit;
  Courant := Bloc;
  Longueur := lstrlenW(Courant);
  while Longueur > 0 do
  begin
    Entree := StringOfChar(' ', Longueur);
    lstrcpynW(Entree, Courant, Longueur + 1);
    { Les entrees « =C:=C:\... » (dossier courant par lecteur) commencent par '=' : leur nom va jusqu'au deuxieme. }
    Egal := Pos('=', Copy(Entree, 2, Length(Entree))) + 1;
    if Egal > 1 then
    begin
      SetArrayLength(Result, GetArrayLength(Result) + 1);
      Result[GetArrayLength(Result) - 1] := Copy(Entree, 1, Egal - 1);
    end;
    Courant := Courant + Longword((Longueur + 1) * 2);
    Longueur := lstrlenW(Courant);
  end;
  FreeEnvironmentStringsW(Bloc);
end;

function EnvironnementPropre(): Boolean;
var
  Noms: TArrayOfString;
  I: Integer;
  W, Temp: String;
begin
  Result := False;
  Noms := NomsDeLEnvironnement();
  { Un bloc illisible laisserait tout l'environnement du compte : l'installation s'arrete. }
  if GetArrayLength(Noms) = 0 then
  begin
    Log('environnement illisible : rien n''est lance');
    Exit;
  end;
  for I := 0 to GetArrayLength(Noms) - 1 do
    if not EffacerVariable(Noms[I], 0) then
    begin
      Log('variable non retiree : ' + Noms[I]);
      Exit;
    end;
  W := ExpandConstant('{win}');
  Temp := W + '\SystemTemp';
  if not DirExists(Temp) then Temp := W + '\Temp';
  SetEnvironmentVariableW('SystemRoot', W);
  SetEnvironmentVariableW('windir', W);
  SetEnvironmentVariableW('SystemDrive', Copy(W, 1, 2));
  SetEnvironmentVariableW('ComSpec', ExpandConstant('{sys}\cmd.exe'));
  SetEnvironmentVariableW('PATH', ExpandConstant('{sys}') + ';' + W + ';' + ExpandConstant('{sys}\Wbem') + ';'
    + ExpandConstant('{sys}\WindowsPowerShell\v1.0'));
  SetEnvironmentVariableW('PATHEXT', '.COM;.EXE;.BAT;.CMD');
  SetEnvironmentVariableW('PSModulePath', ExpandConstant('{sys}\WindowsPowerShell\v1.0\Modules'));
  SetEnvironmentVariableW('OS', 'Windows_NT');
  SetEnvironmentVariableW('TEMP', Temp);
  SetEnvironmentVariableW('TMP', Temp);
  SetEnvironmentVariableW('ProgramFiles', ExpandConstant('{commonpf}'));
  SetEnvironmentVariableW('ProgramData', ExpandConstant('{commonappdata}'));
  SetEnvironmentVariableW('ALLUSERSPROFILE', ExpandConstant('{commonappdata}'));
  Log('environnement reconstruit : ' + IntToStr(GetArrayLength(Noms)) + ' variables retirees, TEMP=' + Temp);
  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnvironnementPropre();
end;

{ --- La desinstallation lance aussi des outils en administrateur (sc, schtasks, taskkill) : meme environnement. --- }
function InitializeUninstall(): Boolean;
begin
  Result := EnvironnementPropre();
end;

{ --- Enregistre le service de mesure PresentMon en MANUEL (start=demand) : il sert
      uniquement a mesurer les FPS, il ne demarre PAS avec Windows (pas de bolt-on
      always-on). L'app le demarre a la demande via le helper eleve. Deja present =
      sc renvoie une erreur inoffensive (loguee, ignoree).
      Le service de demarrage (reglages reappliques au boot) est a part, sur choix :
      voir InstallOrRemoveReconcilerService, plus bas. --- }
{ --- Le dossier de l'installation precedente, lu avant la copie dans la cle de desinstallation d'Inno (vide sinon). La
      0.9.1 acceptait /DIR : ce dossier peut etre hors Program Files, ecrivable par le compte. --- }
var
  AncienDossier: String;

function DossierPrecedent(): String;
begin
  Result := '';
  RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7E4D2A1-9C3F-4E56-8A0D-1F2B3C4D5E6F}_is1', 'Inno Setup: App Path', Result);
end;

{ Le nom PresentMonService est aussi celui du PresentMon d'Intel et d'autres outils : seul un service dont la ligne
  commence par un PresentMonService.exe entre guillemets, sous un dossier DrDXT ou dans le dossier de l'installation
  precedente, est a nous. Une enveloppe qui le cite plus loin ne l'est pas. Meme regle que PresentMonServicePlan dans Core. }
function ImagePathPresentMon(): String;
begin
  Result := '';
  RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\{#PmService}', 'ImagePath', Result);
end;

function PresentMonEstANous(): Boolean;
var
  Chemin: String;
  Fin: Integer;
begin
  Result := False;
  Chemin := Uppercase(Trim(ImagePathPresentMon()));
  if (Length(Chemin) < 2) or (Chemin[1] <> '"') then Exit;
  Chemin := Copy(Chemin, 2, Length(Chemin));
  Fin := Pos('"', Chemin);
  if Fin < 23 then Exit;
  Chemin := Copy(Chemin, 1, Fin - 1);
  Result := ((Pos('\DRDXT\', Chemin) > 0) and (Copy(Chemin, Length(Chemin) - 21, 22) = '\PRESENTMONSERVICE.EXE'))
    or ((AncienDossier <> '') and (CompareText(Chemin, Uppercase(AddBackslash(AncienDossier) + 'PresentMonService.exe')) = 0));
end;

procedure InstallPresentMonService();
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('EtatServiceMesure');
  { --- Les arguments viennent de PresentMonNaming (DrDxt.Core), et ils sont VIDES : mesure
        faite, le serveur PresentMon publie le tube par defaut quoi qu'on lui passe.
        Un test d'integration relit cette ligne et la compare a la constante, pour que les
        deux ecritures ne puissent pas deriver.
        Attention : les \" autour du chemin sont OBLIGATOIRES des que le binPath porte des arguments :
        sans eux sc.exe rend 1639 et ne cree rien, et le repertoire d'installation par
        defaut contient une espace. Ils sont inoffensifs quand la liste est vide, et c'est
        la forme qui survivra au jour ou un argument reviendra.
        (Ne PAS ecrire une constante entre accolades ici : elle fermerait ce commentaire.) --- }
  Exec(ExpandConstant('{sys}\sc.exe'),
       'create {#PmService} binPath= "\"' + ExpandConstant('{app}\PresentMonService.exe') +
       '\"" start= demand DisplayName= "PresentMon Service"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('sc create {#PmService} rc=' + IntToStr(ResultCode));
  { --- « deja present » n'est PAS inoffensif. Mesure sur le poste de dev : un service existant
        qui pointe vers un ancien dossier supprime fait echouer create (1073, ERROR_SERVICE_EXISTS)
        et l'installation laisserait la mesure cassee. Meme cas chez un joueur qui reinstalle ou met a jour.
        On repointe donc le service existant sur CE binaire, sans le supprimer. --- }
  if (ResultCode = 1073) and not PresentMonEstANous() then
    Log('service {#PmService} d''un autre produit : laisse tel quel (' + ImagePathPresentMon() + ')')
  else if ResultCode = 1073 then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'),
         'config {#PmService} binPath= "\"' + ExpandConstant('{app}\PresentMonService.exe') +
         '\"" start= demand',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('sc config {#PmService} rc=' + IntToStr(ResultCode));
  end;
end;

{ --- Le service de demarrage, sur choix seulement (case "reconciler"). Il tourne en SYSTEM
      depuis le sous-dossier service (Program Files : seul un administrateur y ecrit) et ne reapplique que ce que
      le manifeste signe par la cle de la machine autorise. Case cochee : cree (ou repointe s'il existe,
      1073), puis demarre. Case decochee : retire celui qu'une installation precedente aurait pose, pour
      que le choix du moment fasse foi. --- }
procedure InstallOrRemoveReconcilerService();
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('reconciler') then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('EtatServiceDemarrage');
    Exec(ExpandConstant('{sys}\sc.exe'),
         'create {#RcService} binPath= "\"' + ExpandConstant('{app}\service\DrDxt.ReconcilerHost.exe') +
         '\"" start= auto DisplayName= "DrDXT - réglages au démarrage"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('sc create {#RcService} rc=' + IntToStr(ResultCode));
    if ResultCode = 1073 then
    begin
      Exec(ExpandConstant('{sys}\sc.exe'),
           'config {#RcService} binPath= "\"' + ExpandConstant('{app}\service\DrDxt.ReconcilerHost.exe') +
           '\"" start= auto DisplayName= "DrDXT - réglages au démarrage"',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Log('sc config {#RcService} rc=' + IntToStr(ResultCode));
    end;
    { Un service qui plante ne reapplique plus rien jusqu'au prochain demarrage : Windows le relance deux fois, une
      minute d'ecart, puis le laisse arrete ; le compte repart apres une journee. Un arret voulu ne relance rien. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'failure {#RcService} reset= 86400 actions= restart/60000/restart/60000',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('sc failure {#RcService} rc=' + IntToStr(ResultCode));
    Exec(ExpandConstant('{sys}\sc.exe'), 'start {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('sc start {#RcService} rc=' + IntToStr(ResultCode));
  end
  else
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('Service {#RcService} non choisi : retire s''il existait (rc=' + IntToStr(ResultCode) + ').');
  end;
end;

{ --- Le seul dossier admis : Program Files\DrDXT lui-meme. Ailleurs (option /DIR), le dossier herite des droits de son
      parent : sous C:\, tout compte authentifie modifie les fichiers que les services SYSTEM lancent, et /reset ne fait
      que recopier ces droits. Comparaison sans casse, separateur final compris, apres resolution des .. ; un nom court,
      un chemin reseau ou un sous-dossier ne s'ecrivent pas pareil et sont refuses. Le parent est alors Program Files
      lui-meme : seul le dossier pourrait etre une jonction ou un lien qui mene ailleurs ($400, point d'analyse). --- }
function DossierAdmis(): Boolean;
var
  Trouve: TFindRec;
begin
  Result := CompareText(AddBackslash(ExpandFileName(ExpandConstant('{app}'))), AddBackslash(ExpandConstant('{commonpf}\DrDXT'))) = 0;
  if Result and FindFirst(ExpandConstant('{app}'), Trouve) then
  begin
    Result := (Trouve.Attributes and $400) = 0;
    FindClose(Trouve);
  end;
end;

{ --- Le lanceur natif, verifie par empreinte puis lance eleve depuis le fichier meme qui a ete verifie. Il prepare les
      dossiers de confiance en natif (aucun processus enfant, donc rien ne lit l'environnement du compte, aucune strategie
      de langage ne l'empeche), lit proprietaire, regles et points d'analyse sans suivre un lien, ecarte ce qui n'est pas
      fiable et fait naitre le dossier neuf avec son descripteur. Le dossier temporaire de l'installeur est sous le compte :
      le lanceur y est extrait, puis copie dans un sous-dossier de Windows\SystemTemp, que seuls SYSTEM et les
      Administrateurs ecrivent ; l'empreinte de compilation est relue sur cette copie, et c'est elle qui est lancee. Sans
      SystemTemp, rien n'est lance et l'installation s'arrete. --- }
function DossierDuLanceur(): String;
begin
  Result := ExpandConstant('{win}\SystemTemp\DrDXT-installeur');
end;

function LanceurPret(): String;
var
  Source, Chemin: String;
begin
  Result := '';
  if not DirExists(ExpandConstant('{win}\SystemTemp')) then
  begin
    Log('Windows\SystemTemp absent : le lanceur ne se lance pas');
    Exit;
  end;
  Chemin := DossierDuLanceur() + '\DrDxt.Lanceur.exe';
  Source := ExpandConstant('{tmp}\DrDxt.Lanceur.exe');
  if not FileExists(Source) then ExtractTemporaryFile('DrDxt.Lanceur.exe');
  if not FileExists(Source) or not ForceDirectories(DossierDuLanceur()) or not CopyFile(Source, Chemin, False) then
  begin
    Log('lanceur non copie dans SystemTemp : refuse');
    Exit;
  end;
  if CompareText(GetSHA256OfFile(Chemin), '{#EmpreinteLanceur}') <> 0 then
  begin
    Log('lanceur d''empreinte differente : refuse');
    Exit;
  end;
  Result := Chemin;
end;

{ --- La copie du lanceur part a la fin de l'installation. --- }
procedure DeinitializeSetup();
begin
  DeleteFile(DossierDuLanceur() + '\DrDxt.Lanceur.exe');
  RemoveDir(DossierDuLanceur());
end;

{ --- Program Files\DrDXT : SYSTEM et Administrateurs en controle total, Utilisateurs en lecture, DROITS DU PROPRIETAIRE
      en lecture (le compte qui copie un fichier n'en recoit pas le WRITE_DAC implicite), heritable, protegee.
      ProgramData\DrDxt : celle de TrustedManifestRoot.BuildHardenedSecurity. Proprietaire Administrateurs. --- }
const
  SddlApp = 'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;OW)';
  SddlDonnees = 'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)';

var
  DossierEcarte: String;

{ --- Lance le lanceur natif en mode droits ; vrai sur le code 0 seulement. L'empreinte du lanceur est relue avant. --- }
function Droits(Mode, Chemin, Sddl: String): Boolean;
var
  Lanceur: String;
  Sortie: TExecOutput;
  ResultCode, I: Integer;
begin
  Lanceur := LanceurPret();
  if Lanceur = '' then Exit;
  Result := ExecAndCaptureOutput(Lanceur, 'droits ' + Mode + ' "' + Chemin + '" "' + Sddl + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode, Sortie) and (ResultCode = 0);
  for I := 0 to GetArrayLength(Sortie.StdOut) - 1 do
  begin
    Log('droits ' + Mode + ' : ' + Sortie.StdOut[I]);
    if (Mode = 'app') and (Copy(Sortie.StdOut[I], 1, 7) = 'ecarte ') then
      DossierEcarte := Copy(Sortie.StdOut[I], 8, Length(Sortie.StdOut[I]));
  end;
  Log('droits ' + Mode + ' rc=' + IntToStr(ResultCode));
end;

{ --- Le dossier de l'App, AVANT la copie (PrepareToInstall) : fiable, ou neuf et verrouille. Les fichiers que [Files]
      pose appartiennent au compte qui installe ; dans ce dossier, DROITS DU PROPRIETAIRE et la DACL protegee leur
      retirent tout droit d'ecriture du compte pendant l'extraction. --- }
function PreparerLeDossier(): Boolean;
begin
  Result := DossierAdmis() and Droits('app', ExpandConstant('{app}'), SddlApp) and DossierAdmis();
end;

{ --- Le helper vit-il encore ? tasklist filtre par nom d'image ; une ligne CSV qui le porte commence par ce nom entre
      guillemets (le message « aucune tache », traduit, ne commence jamais ainsi). --- }
function HelperVivant(): Boolean;
var
  Sortie: TExecOutput;
  ResultCode, I: Integer;
begin
  Result := False;
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\tasklist.exe'), '/FI "IMAGENAME eq DrDxt.ElevatedHelper.exe" /FO CSV /NH',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode, Sortie) then Exit;
  for I := 0 to GetArrayLength(Sortie.StdOut) - 1 do
    if Pos('"drdxt.elevatedhelper.exe"', Lowercase(Sortie.StdOut[I])) = 1 then Result := True;
end;

{ --- L'App fermee de force (mise a jour, desinstallation) ne rend pas ses reglages a bail : son helper les rend a
      l'expiration du bail (120 s pour le ventilateur Expert), puis s'arrete seul, son client parti. Tue tout de suite, il
      laisserait le ventilateur, le decalage de la carte, la sous-tension, la limite de puissance ou le plafond d'envoi poses
      jusqu'au redemarrage. L'App fermee, il a ce temps :
      135 s au plus (le bail le plus long, le pas de 5 s de sa surveillance, une marge) ; force seulement apres. --- }
procedure LaisserLeHelperRendreSesBaux();
var
  ResultCode, Attendu: Integer;
begin
  { Le helper de la tache « DrDXT helper » (--service) ne s'arrete pas sur inactivite : ce mutex, qui vit jusqu'a la fin de
    l'installeur, lui dit de s'arreter, baux rendus (Program.MutexDeLInstalleur). }
  CreateMutex('DrDxtInstalleur');
  if not HelperVivant() then Exit;
  Log('helper en vie : il rend ses baux et s''arrete seul, 135 s au plus');
  Attendu := 0;
  while HelperVivant() and (Attendu < 135) do
  begin
    Sleep(1000);
    Attendu := Attendu + 1;
  end;
  if HelperVivant() then
  begin
    Log('helper encore en vie apres ' + IntToStr(Attendu) + ' s : arret force');
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM DrDxt.ElevatedHelper.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else
    Log('helper arrete seul apres ' + IntToStr(Attendu) + ' s');
end;

{ --- ProgramData\DrDxt porte ce que SYSTEM croit (cle et manifeste signe, pause du maintien, temoins, journaux du helper).
      ProgramData laisse les comptes y creer des dossiers : un programme du compte la creait d'avance et y posait une
      jonction ou un fichier piege. Elle se prepare ici, avant tout, par le script des droits (mode donnees). --- }
function VerrouillerLesDonnees(): Boolean;
begin
  Result := Droits('donnees', ExpandConstant('{commonappdata}\DrDxt'), SddlDonnees);
end;

{ --- sc stop rend la main avant que le service soit arrete : son .exe tient encore le dossier, et le deplacement qui suit
      echouait (VerrouImpossible). Attend l'etat STOPPED, au plus Secondes ; un service absent (1060) est arrete. --- }
function AttendreLArret(Service: String; Secondes: Integer): Boolean;
var
  Sortie: TExecOutput;
  ResultCode, I, Tour: Integer;
begin
  Result := False;
  for Tour := 0 to Secondes * 2 do
  begin
    if not ExecAndCaptureOutput(ExpandConstant('{sys}\sc.exe'), 'query ' + Service, '', SW_HIDE, ewWaitUntilTerminated,
         ResultCode, Sortie) then Break;
    if ResultCode = 1060 then
    begin
      Result := True;
      Exit;
    end;
    for I := 0 to GetArrayLength(Sortie.StdOut) - 1 do
      if Pos('STOPPED', Sortie.StdOut[I]) > 0 then
      begin
        Result := True;
        Exit;
      end;
    Sleep(500);
  end;
  Log(Service + ' pas encore arrete apres ' + IntToStr(Secondes) + ' s');
end;

{ --- Une mise a jour remplace le sous-dossier service : le service en cours d'execution verrouillerait ses
      fichiers. On l'arrete avant la copie ; InstallOrRemoveReconcilerService le relance s'il est choisi. --- }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  AncienDossier := DossierPrecedent();
  { La tache d'une installation precedente pointe vers son dossier, et a pu tourner au niveau le plus eleve : elle part avant
    tout refus, et la tache sans droits est recreee apres la copie, dossier protege. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT releve"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not DossierAdmis() then
  begin
    Result := FmtMessage(CustomMessage('HorsProgramFiles'), [ExpandConstant('{commonpf}\DrDXT')]);
    Exit;
  end;
  { L'assistant de session ne se relance pas pendant la copie ; PoserOuRetirerLeHelper le repose si la case reste cochee. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT helper"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  AttendreLArret('{#RcService}', 30);
  { Le releve de fond tourne sans fenetre a chaque session ; sans ceci, une mise a jour butait sur
    DrDxt.App.exe en cours d'utilisation. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { Le helper et notre service PresentMon tiennent aussi leurs .exe du dossier de l'App ouverts. Celui d'un autre produit
    (meme nom) continue ; le notre est en demarrage a la demande, l'App le relance quand elle mesure. }
  LaisserLeHelperRendreSesBaux();
  if PresentMonEstANous() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PmService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    AttendreLArret('{#PmService}', 30);
  end;
  if not DirExists(ExpandConstant('{win}\SystemTemp')) then
  begin
    Result := CustomMessage('SystemTempAbsent');
    Exit;
  end;
  if not PreparerLeDossier() then
  begin
    Result := CustomMessage('VerrouImpossible');
    Exit;
  end;
  if not VerrouillerLesDonnees() then
  begin
    Result := CustomMessage('DonneesImpossibles');
    Exit;
  end;
  Result := '';
end;

{ --- Le fichier copie est-il celui que l'installeur porte ? Absent ou illisible : non. --- }
function Conforme(Fichier, Attendue: String): Boolean;
begin
  try
    Result := CompareText(GetSHA256OfFile(ExpandConstant('{app}\') + Fichier), Attendue) = 0;
  except
    Result := False;
  end;
  if not Result then Log('empreinte differente ou illisible : ' + Fichier);
end;

{ --- Defense en profondeur, apres le verrou : chaque executable lance en SYSTEM ou eleve, et la DLL qu'il charge, porte
      l'empreinte calculee a la compilation. Un ecart : ni services ni tache, et le joueur le lit. --- }
function EmpreintesConformes(): Boolean;
begin
  Result := Conforme('service\DrDxt.ReconcilerHost.exe', '{#EmpreinteHote}')
    and Conforme('service\DrDxt.ReconcilerHost.dll', '{#EmpreinteHoteDll}')
    and Conforme('PresentMonService.exe', '{#EmpreintePresentMon}')
    and Conforme('DrDxt.ElevatedHelper.exe', '{#EmpreinteHelper}')
    and Conforme('DrDxt.ElevatedHelper.dll', '{#EmpreinteHelperDll}')
    and Conforme('DrDxt.Lanceur.exe', '{#EmpreinteLanceur}')
    and Conforme('DrDxt.App.exe', '{#EmpreinteApp}')
    and Conforme('DrDxt.App.dll', '{#EmpreinteAppDll}');
end;

{ --- Apres la copie, avant tout service et toute tache. L'arbre est fiable depuis PreparerLeDossier : le compte n'y
      ecrit rien et rien n'y mene ailleurs, un /setowner recursif y est sur. Les fichiers copies passent aux
      Administrateurs (CheminProtege refuse un executable dont le compte est proprietaire), puis l'arbre est relu, puis
      les empreintes. Le dossier lui-meme garde son proprietaire : en changer retirerait sa regle DROITS DU PROPRIETAIRE.
      Un echec ne demarre rien sur des fichiers remplacables. --- }
function ProtegerLeDossier(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '\*" /setowner *S-1-5-32-544 /T /C /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('icacls setowner des fichiers copies rc=' + IntToStr(ResultCode));
  Result := DossierAdmis() and Droits('verifier', ExpandConstant('{app}'), '') and EmpreintesConformes();
end;

{ --- L'ancien dossier, s'il n'est pas celui-ci, reste sur le disque : plus rien n'y pointe (tache remplacee, services
      repointes, raccourcis recrees). Il n'est pas supprime en administrateur : ecrivable par le compte, une jonction y
      menerait ailleurs. Le joueur le lit. --- }
procedure SignalerLAncienDossier();
begin
  if (AncienDossier = '') or (CompareText(AddBackslash(AncienDossier), AddBackslash(ExpandConstant('{app}'))) = 0)
     or not DirExists(AncienDossier) then Exit;
  Log('ancienne installation laissee sur le disque : ' + AncienDossier);
  SuppressibleMsgBox(FmtMessage(CustomMessage('AncienDossierReste'), [AncienDossier]), mbInformation, MB_OK, IDOK);
end;

function XmlEchappe(S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
end;

{ --- Le releve des parties, a chaque ouverture de session, pour le compte qui l'ouvre et sans droits : le principal est le
      groupe Utilisateurs (S-1-5-32-545) en LeastPrivilege, jamais le compte qui a fait l'elevation. Un compte standard
      installe avec le mot de passe d'un administrateur : une tache creee sans principal serait celle de l'administrateur,
      et ne releverait jamais les parties du joueur. Une instance par session (Parallel), pour un PC a plusieurs comptes ; sur
      batterie aussi, et sans limite de duree : le releve tient toute la session. Le
      fichier XML vit dans le sous-dossier de Windows\SystemTemp que seuls SYSTEM et les Administrateurs ecrivent, et part
      apres la creation. schtasks ne lit l'UTF-8 que sans BOM et sans declaration d'encodage : sinon « XML mal forme ». --- }
function PoserLeReleve(): Boolean;
var
  Chemin: String;
  Xml: TArrayOfString;
  ResultCode: Integer;
begin
  Result := False;
  ResultCode := -1;
  Chemin := DossierDuLanceur() + '\releve.xml';
  SetArrayLength(Xml, 1);
  Xml[0] := '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' +
    '<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>' +
    '<Principals><Principal id="Joueur"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>' +
    '<Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>' +
    '<Actions Context="Joueur"><Exec><Command>' + XmlEchappe(ExpandConstant('{app}') + '\DrDxt.App.exe') +
    '</Command><Arguments>--releve</Arguments></Exec></Actions></Task>';
  if ForceDirectories(DossierDuLanceur()) and SaveStringsToUTF8FileWithoutBOM(Chemin, Xml, False) then
    Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Create /F /TN "DrDXT releve" /XML "' + Chemin + '"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  DeleteFile(Chemin);
  if not Result then Log('releve : tache par groupe refusee (' + IntToStr(ResultCode) + ')');
end;

function WTSQuerySessionInformationW(Serveur: Longword; Session: Longword; Classe: Integer; var Tampon: Longword;
  var Octets: Longword): Boolean; external 'WTSQuerySessionInformationW@wtsapi32.dll stdcall';
procedure WTSFreeMemory(Memoire: Longword); external 'WTSFreeMemory@wtsapi32.dll stdcall';

{ --- Le compte d'origine : celui de la session interactive, qui a lance l'installeur. Un administrateur qui valide l'UAC
      pour un joueur sans droits (« par-dessus l'epaule ») donne son jeton a l'installeur, pas sa session : GetUserNameString
      dirait l'administrateur. Lu a l'API de la session (WTS), jamais dans un fichier ou une variable que le compte ecrit. --- }
function LireLaSession(Classe: Integer): String;
var
  Tampon, Octets: Longword;
  Longueur: Integer;
begin
  Result := '';
  Tampon := 0;
  if not WTSQuerySessionInformationW(0, $FFFFFFFF, Classe, Tampon, Octets) then Exit;
  Longueur := lstrlenW(Tampon);
  if Longueur > 0 then
  begin
    Result := StringOfChar(' ', Longueur);
    lstrcpynW(Result, Tampon, Longueur + 1);
  end;
  WTSFreeMemory(Tampon);
end;

{ Vide si la session n'a pas de compte (installation deployee en session 0) : aucune tache n'est alors posee. }
function CompteDOrigine(): String;
var
  Nom: String;
begin
  Result := '';
  Nom := LireLaSession(5);
  if Nom <> '' then Result := LireLaSession(7) + '\' + Nom;
end;

var
  CompteDOrigineLu: Integer;

{ --- Le compte d'origine est-il administrateur ? Sous son propre jeton (ExecAsOriginalUser, sans droits), whoami liste ses
      groupes : un administrateur filtre par l'UAC y garde S-1-5-32-544 (« utilise pour refus seulement »), un compte
      standard ne l'a pas. Le SID ne depend pas de la langue de Windows. Lu une fois ; illisible = standard, dit au journal.
      Un programme du compte qui fausserait la reponse ne gagne rien : HighestAvailable n'eleve pas un compte standard. --- }
function CompteDOrigineAdmin(): Boolean;
var
  ResultCode: Integer;
begin
  if CompteDOrigineLu = 0 then
  begin
    CompteDOrigineLu := 2;
    try
      if ExecAsOriginalUser(ExpandConstant('{sys}\cmd.exe'), '/S /C ""' + ExpandConstant('{sys}\whoami.exe') + '" /groups | "' +
           ExpandConstant('{sys}\findstr.exe') + '" /C:"S-1-5-32-544""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
         and (ResultCode = 0) then
        CompteDOrigineLu := 1;
    except
      Log('helper : groupes du compte d''origine illisibles (' + GetExceptionMessage + ')');
    end;
    if CompteDOrigineLu = 1 then
      Log('helper : compte d''origine administrateur, la case est proposee')
    else
      Log('helper : compte d''origine standard, la case n''est pas proposee');
  end;
  Result := CompteDOrigineLu = 1;
end;

{ --- L'assistant administrateur pour la session, sur choix seulement (case "helperseance") : les essais d'Optimiser avancent
      DrDXT ferme. Une tache du compte d'origine, a son ouverture de session, au niveau le plus eleve (aucune invite) ; par
      le lanceur, qui reconstruit l'environnement (un processus eleve heriterait de HKCU\Environment, ou tout programme du
      compte peut poser CORECLR_PROFILER_PATH), et avec --app-path, sans quoi le helper refuse tout appelant. Sur batterie
      aussi, sans limite de duree, une seule instance. Decochee, la tache d'une installation precedente part. --- }
procedure PoserOuRetirerLeHelper();
var
  Chemin, Compte: String;
  Xml: TArrayOfString;
  ResultCode: Integer;
begin
  { En silencieux, /TASKS ou /MERGETASKS peut la demander pour un compte standard : la case n'existe pas, la demande est
    refusee et dite au journal. }
  if not CompteDOrigineAdmin() and (Pos('helperseance', Lowercase(ExpandConstant('{param:TASKS|}') + ',' +
       ExpandConstant('{param:MERGETASKS|}'))) > 0) then
    Log('helper : tache demandee en ligne de commande, refusee : le compte d''origine n''est pas administrateur');
  Compte := CompteDOrigine();
  if not WizardIsTaskSelected('helperseance') or (Compte = '') then
  begin
    if Compte = '' then Log('helper : aucun compte dans la session, tache non posee');
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT helper"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exit;
  end;
  ResultCode := -1;
  Compte := XmlEchappe(Compte);
  Chemin := DossierDuLanceur() + '\helper.xml';
  SetArrayLength(Xml, 1);
  Xml[0] := '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' +
    '<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>' + Compte + '</UserId></LogonTrigger></Triggers>' +
    '<Principals><Principal id="Joueur"><UserId>' + Compte + '</UserId><LogonType>InteractiveToken</LogonType>' +
    '<RunLevel>HighestAvailable</RunLevel></Principal></Principals>' +
    '<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>' +
    '<Actions Context="Joueur"><Exec><Command>' + XmlEchappe(ExpandConstant('{app}') + '\DrDxt.Lanceur.exe') +
    '</Command><Arguments>DrDxt.ElevatedHelper.exe &quot;--app-path=' + XmlEchappe(ExpandConstant('{app}') + '\{#AppExe}') +
    '&quot; --service</Arguments></Exec></Actions></Task>';
  if not (ForceDirectories(DossierDuLanceur()) and SaveStringsToUTF8FileWithoutBOM(Chemin, Xml, False)
          and Exec(ExpandConstant('{sys}\schtasks.exe'), '/Create /F /TN "DrDXT helper" /XML "' + Chemin + '"', '', SW_HIDE,
                   ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    Log('helper : tache non posee (' + IntToStr(ResultCode) + ')');
  DeleteFile(Chemin);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ReleveDeTous: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    if ProtegerLeDossier() then
    begin
      InstallPresentMonService();
      InstallOrRemoveReconcilerService();
      PoserOuRetirerLeHelper();
      { Le releve des parties, sans droits. Eleve, il ecrirait dans le dossier du compte, ou un programme sans droits peut
        poser une jonction : creation, ecrasement ou suppression de fichier en administrateur a chaque ouverture de session.
        Sans droits, il lit le processeur par le service DrDXT s'il est installe, comme l'App. L'App directement, pas le
        lanceur : non eleve, celui-ci demanderait l'elevation (une invite a chaque ouverture de session). Si Windows refuse
        la tache par groupe, celle du compte qui installe (/RL LIMITED) la remplace : elle releve au moins ce compte. Le
        premier releve part tout de suite, sous le compte qui a lance l'installeur ; c'est le joueur quand l'installeur
        s'est eleve lui-meme. }
      { Une version precedente de l'App posait le raccourci « DrDXT releve » dans le Demarrage du joueur, sous son compte :
        la tache le remplace. Il s'efface sous le compte qui a lance l'installeur, sans droits et dans son propre profil.
        Le Demarrage de l'installeur eleve est celui du compte qui valide l'UAC, un autre pour un joueur sans droits. }
      ExecAsOriginalUser(ExpandConstant('{sys}\cmd.exe'), '/c del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DrDXT releve.lnk"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      ReleveDeTous := PoserLeReleve();
      if ReleveDeTous or (Exec(ExpandConstant('{sys}\schtasks.exe'), '/Create /F /TN "DrDXT releve" /SC ONLOGON /RL LIMITED /TR "\"' + ExpandConstant('{app}') + '\DrDxt.App.exe\" --releve"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
      begin
        { Le repli redonne la tache du compte qui installe : dit, pour que le defaut ne revienne pas sans bruit. }
        if not ReleveDeTous then
          SuppressibleMsgBox(CustomMessage('ReleveCeCompte'), mbInformation, MB_OK, IDOK);
        ExecAsOriginalUser(ExpandConstant('{app}\DrDxt.App.exe'), '--releve', '', SW_HIDE, ewNoWait, ResultCode);
      end
      else
      begin
        Log('releve : aucune tache posee (' + IntToStr(ResultCode) + ')');
        SuppressibleMsgBox(CustomMessage('ReleveNonPose'), mbError, MB_OK, IDOK);
      end;
    end
    else
    begin
      { Dossier non protege : aucune tache elevee, et celle d'une installation precedente est retiree, comme ses services :
        celui de demarrage repartirait en SYSTEM au prochain demarrage, sur ces fichiers. }
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT releve"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT helper"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if PresentMonEstANous() then
      begin
        Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PmService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#PmService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end;
      SuppressibleMsgBox(CustomMessage('ProtectionEchouee'), mbError, MB_OK, IDOK);
    end;
    SignalerLAncienDossier();
    if DossierEcarte <> '' then
      SuppressibleMsgBox(FmtMessage(CustomMessage('AncienDossierReste'), [DossierEcarte]), mbInformation, MB_OK, IDOK);
  end;
end;

{ --- Les temoins d'incident du service, a la desinstallation : leurs fichiers, puis le dossier vide. Une jonction posee a
      la place du dossier, ou d'un fichier, n'est jamais suivie. --- }
procedure SupprimerLesTemoins();
var
  Dossier: String;
  Trouve: TFindRec;
  Lien: Boolean;
begin
  Dossier := ExpandConstant('{commonappdata}\DrDxt\temoins');
  if not FindFirst(Dossier, Trouve) then Exit;
  Lien := (Trouve.Attributes and $400) <> 0;
  FindClose(Trouve);
  if Lien then
  begin
    RemoveDir(Dossier);
    Exit;
  end;
  if FindFirst(Dossier + '\*', Trouve) then
  try
    repeat
      if ((Trouve.Attributes and $10) = 0) and ((Trouve.Attributes and $400) = 0) then
        DeleteFile(Dossier + '\' + Trouve.Name);
    until not FindNext(Trouve);
  finally
    FindClose(Trouve);
  end;
  RemoveDir(Dossier);
end;

{ --- Desinstallation : coupe l'app + le helper s'ils tournent, retire le service. usUninstall
      tourne AVANT [UninstallRun] (le rendu des reglages), qui tourne avant le retrait des fichiers :
      voir la section [UninstallRun]. --- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { La tache planifiee du releve (voir [Run]) part avec le programme, et avant lui : une ouverture de session pendant
      l'attente du helper (jusqu'a 135 s) relancerait l'App, dont le fichier resterait alors jusqu'au redemarrage. }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT releve"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    { L'assistant de session aussi : il s'arrete ensuite, baux rendus (LaisserLeHelperRendreSesBaux). }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "DrDXT helper"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F',                '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    { Une App elevee tuee en pleine mesure laisse la session ETW du compteur DXGI ouverte jusqu'au redemarrage. }
    Exec(ExpandConstant('{sys}\logman.exe'), 'stop DrDxtPresents -ets',          '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    LaisserLeHelperRendreSesBaux();
    { Le service de demarrage d'abord, arrete pour de vrai (sc stop rend la main avant) : plus rien ne doit reappliquer
      pendant le rendu des reglages. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    AttendreLArret('{#RcService}', 30);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('Uninstall: service {#RcService} arrete + supprime (rc=' + IntToStr(ResultCode) + ').');
    { Seulement le notre : celui d'un autre produit (meme nom) reste. }
    if CompareText(ImagePathPresentMon(), '"' + ExpandConstant('{app}\PresentMonService.exe') + '"') = 0 then
    begin
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PmService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      AttendreLArret('{#PmService}', 30);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#PmService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Log('Uninstall: service {#PmService} arrete + supprime.');
    end
    else
      Log('Uninstall: service {#PmService} d''un autre produit, laisse tel quel.');
    { La source de l'Observateur d'evenements que l'hote du service creait a son premier avertissement : le service n'en
      cree plus, et celle d'une version precedente part avec lui. }
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\DrDxt.ReconcilerHost');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { Le manifeste est renomme `.rendu-<heure>` quand tout a ete rendu ; s'il reste, une origine n'a pas ete remise
      (ecriture refusee, cible illisible, helper qui n'a pas demarre). Ce qui n'est pas au manifeste (une ligne du journal en
      suspens, la carte, le plafond d'envoi, un programme au demarrage, le delai depasse) pose la marque `rendu-incomplet`.
      Le joueur le lit, et le dossier reste pour reessayer. }
    if FileExists(ExpandConstant('{commonappdata}\DrDxt\desired-state.manifest'))
       or FileExists(ExpandConstant('{commonappdata}\DrDxt\rendu-incomplet')) then
      SuppressibleMsgBox(FmtMessage(CustomMessage('RenduIncomplet'), [ExpandConstant('{commonappdata}\DrDxt')]), mbInformation, MB_OK, IDOK);
    SupprimerLesTemoins();
  end;
end;
