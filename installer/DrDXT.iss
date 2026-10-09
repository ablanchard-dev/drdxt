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
; VIDE, ET C'EST UNE MESURE, PAS UN OUBLI. Le 07/09/2026, cette ligne a porte pendant
; quelques heures `--control-pipe \\.\pipe\drdxt-presentmon-control` : l'idee etait de
; donner a DrDXT un tube prive pour cohabiter avec le service Intel. Trois mesures par le
; SCM l'ont demolie -- le serveur accepte l'option et publie quand meme le nom par defaut,
; en 2.4.1 comme avec le binaire Intel 2.5.1. Voir docs/PRESENTMON-COEXISTENCE.md.
;
; (Au passage, mesure avec ISCC : le preprocesseur d'Inno consomme un antislash sur deux
; dans les chaines, guillemets simples ET doubles. Tout argument futur contenant un chemin
; devra doubler ses antislash, et le test le verifie.)
#define PmServiceArgs ""
; Le service qui reapplique les reglages au demarrage. Doit rester identique au ServiceName de
; l'hote, src/DrDxt.ReconcilerHost/Program.cs (test d'integration).
#define RcService "DrDxtReconciler"

[Setup]
AppId={{B7E4D2A1-9C3F-4E56-8A0D-1F2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVer}
DefaultDirName={autopf}\DrDXT
; 25/09/2026 (revue de sécurité) : le dossier ne se choisit pas. Hors de Program Files (C:\DrDXT hérite
; des droits de C:\), un compte sans droits admin pouvait remplacer le helper, lancé élevé à la demande
; et par la désinstallation. UsePreviousAppDir=no : une mise à jour ne reprend pas un ancien dossier choisi.
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=DrDXT
DisableProgramGroupPage=yes
AllowNoIcons=yes
; 08/10/2026 (PUB1) : la licence GPL v3 se lit et s'accepte dans l'installeur.
LicenseFile=..\LICENSE
OutputDir=Output
; Le nom DIT ce que le fichier est : build-installer.ps1 passe DrDXT-Setup-NON-SIGNE
; tant qu'aucun certificat n'est utilise.
#ifndef OutBase
  #define OutBase "DrDXT-Setup-NON-SIGNE"
#endif
OutputBaseFilename={#OutBase}
; 27/09/2026 : le service embarque son propre runtime .NET, 223 fichiers identiques a ceux de l'App
; (87 Mo). Avec le dictionnaire par defaut, trop court pour voir les deux copies, l'installeur pesait
; 73,1 Mo ; avec un dictionnaire plus grand que le contenu (238 Mo), la seconde copie ne coute presque
; rien : 45,1 Mo mesures. Prix : l'installation demande autant de memoire que le dictionnaire.
Compression=lzma2/ultra64
LZMADictionarySize=262144
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
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
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"
; 27/09/2026 : decochee par defaut. Un service SYSTEM qui demarre avec Windows ne s'installe pas sans
; avoir ete choisi. Sans lui, les reglages restent appliques mais ne sont pas reverifies au demarrage.
Name: "reconciler"; Description: "Réappliquer mes réglages au démarrage de Windows (service DrDxtReconciler)"; GroupDescription: "Démarrage :"; Flags: unchecked

[InstallDelete]
; 27/09/2026 : le raccourci « Desinstaller DrDXT » prend ses accents ; sans cette ligne, une mise a jour
; laisserait l'ancien a cote du nouveau dans le menu Demarrer.
Type: files; Name: "{group}\Desinstaller DrDXT.lnk"
; 27/09/2026 : les fichiers de symboles ne sont plus livres depuis le 18/09, mais une mise a jour
; n'efface que ce qu'elle remplace. Chez le mainteneur, 12 .pdb du 14/09 restaient a cote des binaires du 27/09.
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\service\*.pdb"

[UninstallDelete]
; 06/10/2026 (J1b) : le raccourci du releve des parties, pose par l'App dans le dossier Demarrage du joueur
; (ReleveAuDemarrage.NomRaccourci). Sans ce retrait, Windows lancerait a chaque session un programme desinstalle.
Type: files; Name: "{userstartup}\DrDXT releve.lnk"

[Files]
; Payload complet (App + helper + runtime + DLLs PresentMon + PresentMonService.exe + NOTICE)
; 18/09/2026 - Excludes "*.pdb" : mesure du payload construit, 13 fichiers de symboles, 576 Ko.
; Un .pdb ne porte pas que des numeros de ligne : lu octet par octet, celui du helper eleve contient
; 57 noms de fichiers .cs et le chemin de l'arborescence de build jusqu'au dossier personnel de son
; auteur. Sur un produit ferme dont le moat est l'allowlist default-DENY du helper, c'est le plan du
; coffre livre avec le coffre. Les symboles restent PRODUITS (on ne passe pas DebugType=none) : ils
; servent a lire une pile d'appel cote build. C'est leur livraison qui est refusee, pas leur existence.
; 18/09/2026 - Excludes "\third_party" : mesure du payload construit, PresentMonService.exe y est DEUX
; fois, 3,6 Mo chacun, empreinte identique. La racine vient de l'etape 3 de build-installer.ps1 (c'est
; elle que [Code] enregistre) ; celle sous third_party\presentmon\ vient du csproj du helper et sert de
; repli EN DEVELOPPEMENT. Or le locator essaie la racine d'abord, et l'installeur la garantit : chez le
; joueur cette seconde copie n'est jamais lue. On poserait 3,6 Mo d'un binaire de service que
; l'installeur n'enregistre pas. Le repli reste PRODUIT cote build, c'est sa livraison qui est refusee.
Source: "publish\*";           DestDir: "{app}"; Excludes: "*.pdb,\third_party"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README-testeur.txt";  DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\DrDXT";            Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Désinstaller DrDXT"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DrDXT";      Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[UninstallRun]
; 11/09/2026 - RENDRE LES REGLAGES avant de retirer les fichiers. Ordre REEL de la desinstallation,
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
; [UninstallRun] ; le mode borne lui-meme sa duree (120 s), rend 0, et ecrit son rapport sous
; %ProgramData%\DrDxt\desinstallation-<heure UTC>.txt. skipifdoesntexist : un helper absent n'ajoute
; pas d'avertissement. Un test d'integration relit cette ligne.
Filename: "{app}\DrDxt.ElevatedHelper.exe"; Parameters: "--uninstall-revert"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DrDxtRendreLesReglages"

[Code]
{ --- 25/09/2026 : les outils Windows se lancent par la constante sys d'Inno (System32 64 bits, installeur en
      mode 64 bits), jamais par leur nom seul. Il tourne en administrateur ; un nom seul laisse
      Windows chercher d'abord ailleurs, dont le dossier courant (souvent Telechargements).
      Meme regle que le helper. Un test d'integration refuse un Exec sans chemin. --- }

{ --- Enregistre le service de mesure PresentMon en MANUEL (start=demand) : il sert
      uniquement a mesurer les FPS, il ne demarre PAS avec Windows (pas de bolt-on
      always-on). L'app le demarre a la demande via le helper eleve. Deja present =
      sc renvoie une erreur inoffensive (loguee, ignoree).
      Le service de demarrage (reglages reappliques au boot) est a part, sur choix :
      voir InstallOrRemoveReconcilerService, plus bas. --- }
procedure InstallPresentMonService();
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Enregistrement du service de mesure...';
  { --- Les arguments viennent de PresentMonNaming (DrDxt.Core), et ils sont VIDES : mesure
        du 07/09/2026, le serveur PresentMon publie le tube par defaut quoi qu'on lui passe.
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
  { --- 14/09/2026 : « deja present » n'etait PAS inoffensif. Mesure sur le poste de dev : le service
        existant pointait vers un ancien dossier supprime ; create echouait (1073, ERROR_SERVICE_EXISTS)
        et l'installation laissait la mesure cassee. Meme cas chez un joueur qui reinstalle ou met a jour.
        On repointe donc le service existant sur CE binaire, sans le supprimer. --- }
  if ResultCode = 1073 then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'),
         'config {#PmService} binPath= "\"' + ExpandConstant('{app}\PresentMonService.exe') +
         '\"" start= demand',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('sc config {#PmService} rc=' + IntToStr(ResultCode));
  end;
end;

{ --- 27/09/2026 : le service de demarrage, sur choix seulement (case "reconciler"). Il tourne en SYSTEM
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
    WizardForm.StatusLabel.Caption := 'Enregistrement du service de démarrage...';
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

{ --- Une mise a jour remplace le sous-dossier service : le service en cours d'execution verrouillerait ses
      fichiers. On l'arrete avant la copie ; InstallOrRemoveReconcilerService le relance s'il est choisi. --- }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

{ --- 27/09/2026, trouve par l'audit de DrDXT sur sa propre installation : les fichiers poses
      appartenaient au compte qui avait lance l'installeur, et la regle CREATEUR PROPRIETAIRE de
      Program Files lui donnait le controle total. Ce compte, meme non eleve (UAC active), pouvait
      donc remplacer PresentMonService.exe ou l'hote du service, qui tournent en SYSTEM, et le helper,
      qui tourne en administrateur. La propriete revient aux Administrateurs, puis les droits sont
      recalcules depuis Program Files : Utilisateurs en lecture seule. Avant tout demarrage de service. --- }
{ 28/09/2026 : rend un verdict. Un echec laissait demarrer les services SYSTEM sur des fichiers remplacables. }
function ProtegerLeDossier(): Boolean;
var
  ResultCode: Integer;
  Proprietaire, Droits: Boolean;
begin
  Proprietaire := Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /setowner *S-1-5-32-544 /T /C /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log('icacls setowner Administrateurs rc=' + IntToStr(ResultCode));
  Droits := Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /reset /T /C /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log('icacls reset rc=' + IntToStr(ResultCode));
  Result := Proprietaire and Droits;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if ProtegerLeDossier() then
    begin
      InstallPresentMonService();
      InstallOrRemoveReconcilerService();
    end
    else
      SuppressibleMsgBox('La protection du dossier de DrDXT a échoué : les services de démarrage ne sont pas installés, '
        + 'pour qu''aucun programme de ton compte ne puisse remplacer leurs fichiers. DrDXT fonctionne sans eux ; '
        + 'relance l''installation pour réessayer.', mbError, MB_OK, IDOK);
  end;
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
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F',                '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM DrDxt.ElevatedHelper.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    { Le service de demarrage d'abord : plus rien ne doit reappliquer pendant le rendu des reglages. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#RcService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#RcService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('Uninstall: service {#RcService} arrete + supprime (rc=' + IntToStr(ResultCode) + ').');
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PmService}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#PmService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('Uninstall: service {#PmService} arrete + supprime.');
  end;
end;
