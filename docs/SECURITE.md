# Securite de DrDXT — ce qui est attaquable, et ce qui le tient

Une page. Deux sections : le **tri des avis de vulnerabilite** (ce qui vient de l'exterieur du
depot), et la **table actif x attaquant** (ce que le produit defend lui-meme).

**La regle de cette page** : chaque ligne de la table porte le nom d'un test qui EXISTE. Une ligne
sans test n'est pas une ligne de securite, c'est une intention — et une intention se relit comme une
garantie. `tools/nonuisance/check-guard-tests.py` resout les noms cites ; le tri des avis, lui, est
garde par `TheVulnerabilityTriageIsCompleteGateTests`.

---

## 1. Tri des avis — mesure du 06/09/2026

Sortie brute et complete : [`vuln-2026-09-06.txt`](vuln-2026-09-06.txt). **Trois avis distincts**,
tous de gravite High, tous **transitifs** (aucun n'est un paquet que le depot demande directement).

La question qui tranche n'est pas « quelle gravite » mais **« ce code tourne-t-il chez le client, et
l'attaquant controle-t-il ce qu'il faut controler pour le declencher »**. Une CVE High dans un
paquet que seul `dotnet test` charge n'atteint personne ; une CVE Medium sur le chemin de donnees du
serveur les atteint toutes.

### GHSA-2m69-gcr7-jv3q — `SQLitePCLRaw.lib.e_sqlite3` (CVE-2025-6965)

**RETIRE LE 23/09/2026 — plus aucun SQLite dans le depot.**

- **Ou il etait** : `DrDxt.Server`, qui tenait la base des commandes du paiement (Lemon Squeezy →
  Keygen). DrDXT est devenu gratuit : le webhook de paiement, la base et le paquet SQLite sont
  partis ensemble. Le serveur ne sert plus que le canal de mise a jour signe.
- **Mesure** : `dotnet list package --vulnerable --include-transitive` sur la solution → aucun
  paquet vulnerable dans les projets de production (23/09/2026).
- **Ce qui reste** : la garde `No_server_sql_text_is_built_from_anything_but_a_literal` continue
  de refuser tout `CommandText` construit a la main, si du SQL revenait un jour.

### GHSA-7jgj-8wvc-jh57 — `System.Net.Http` 4.3.0

**RETIRE LE 17/10/2026 — plus mesure.** Les projets de tests sont passes a `xunit 2.9.3` sous .NET 10 : la chaine
ci-dessous n'existe plus. `dotnet list package --vulnerable --include-transitive` sur toute la solution ne trouve
aucun paquet vulnerable, dans aucun projet (`docs/vuln-2026-10-17.txt`). Le tri d'origine reste ci-dessous.

Tri du 06/09/2026 : **TEST-ONLY.**

- **Ou** : 17 projets, **tous des projets `.Tests`** ; **aucun projet de production**.
- **Trace mesuree** : `xunit 2.5.3` -> `xunit.assert` / `xunit.core` -> `NETStandard.Library 1.6.1`
  -> `System.Net.Http 4.3.0`. C'est le harnais de test qui le tire, pas le produit.
- **Verdict** : le code du produit livre au client ne contient pas ce paquet. Il ne s'execute que
  sous `dotnet test`, sur cette machine et dans la CI. Sur `net8.0`, ce paquet n'est de toute facon
  qu'une **facade** : l'implementation utilisee est celle du framework.
- **Residuel dit** : un poste de developpement compromis reste un poste compromis — cette CVE n'y
  change rien, et elle ne la rend ni plus ni moins probable.

### GHSA-cmhx-cq75-c4mj — `System.Text.RegularExpressions` 4.3.0

**RETIRE LE 17/10/2026 — plus mesure**, pour la meme raison (`docs/vuln-2026-10-17.txt`).

Tri du 06/09/2026 : **TEST-ONLY.** Meme trace, meme chaine (`xunit` -> `NETStandard.Library 1.6.1`), memes 17 projets de
tests, aucun projet de production, meme statut de facade sur `net8.0`. Meme verdict.

---

## 2. Actif x attaquant — et le test qui tient chaque ligne

Chaque ligne se lit : *si cet attaquant reussissait, cet actif tomberait ; voici ce qui l'en
empeche, et voici le test qui le prouve.*

| Actif | Attaquant, et ce qu'il controle | Ce qui tient | Preuve |
|---|---|---|---|
| Le manifeste de mise a jour | un **serveur hostile** ou un intermediaire | signature verifiee avant que l'url soit touchee | → test: `UpdatePackageVerifierTests` |
| Le binaire de mise a jour | **payload substitue** apres coup | SHA-256 compare au manifeste **deja signe** ; sinon jamais lance | → test: `AutoUpdaterTests` |
| Le canal de mise a jour | un intermediaire qui **retient** une mise a jour de securite | https exige au montage ; en clair, un refus de servir est invisible pour une signature | → test: `TheUpdaterIsMountedOrSaysWhyNotTests` |
| La couture client-serveur | une **derive** entre les deux formes canoniques, qui refuserait tout en silence | le vrai signeur signe, le vrai verificateur verifie, dans un seul test | → test: `TheServerSignsWhatTheClientVerifiesTests` |
| Les secrets du serveur | quelqu'un qui **decompile** le client | aucun secret dans `src/DrDxt.*` ; balayage statique de l'arbre | → test: `SecurityGateTests` |
| La couche de confiance | un `catch` qui rendrait **succes** sur une signature cassee | anti-fail-open statique sur la mise a jour | → test: `SecurityGateTests` |
| Le tube nomme privilegie | un **client non autorise** qui s'y connecte | authentification de l'appelant avant toute poignee de main, refus par defaut ; corrige le 26/09/2026 : cette ligne citait le test de `PrivilegedPipeServer`, un serveur que le produit ne lance jamais, au lieu de celui du serveur reel | → test: `HandshakeChannelIntegrationTests` |
| Le registre de la machine | un op privilegie **forge** sur le tube | liste blanche feuille-exacte, plus 11 sous-arbres toujours refuses | → test: `OpAllowlistTests` |
| La machine du client | un **op de plus** ajoute par megarde plus tard | surface figee : la liste exacte des noms d'ops vit dans le test, pas ici (un chiffre ecrit a la main a derive de 18 a 38) | → test: `ThePrivilegedSurfaceIsClosedGateTests` |
| Le materiel | une **ecriture de tension** ajoutee plus tard | aucune tension de carte graphique ; une seule tension existe, celle des cœurs et du cache du processeur Intel, vers le bas seulement (`SetCpuVoltageOffsetOp`, 0 a −125 mV) ; le filtre de noms fait tomber toute autre op de tension | → test: `NoVoltageWriteOpExistsGateTests` |
| Le BIOS | le composant qui **guide** dans le BIOS, s'il ecrivait | `DrDxt.Bios` ne reference que `DrDxt.Core` : structurellement incapable | → test: `TheGuideNeverWritesTheBiosGateTests` |
| L'etat desire du reconciliateur | un fichier **altere** sur le disque | signature verifiee, chargement **fail-closed** | → test: `SignedDesiredStateStoreTests` |
| Le rapport SAV | ce que le client **colle** dans un ticket public | profil, nom du PC, adresses, numéros de série et courriels retirés | → test: `ExportDeMachineTests`, `CartesAgirTests` |
| L'installeur | livrer un binaire non signe **en le croyant signe** | sans `-Sign`, l'installeur s'appelle `DrDXT-Setup-NON-SIGNE.exe` ; `-Sign` sans certificat echoue tout de suite, sans repli sur du non signe ; une signature posee est relue avant d'etre annoncee. Un installeur non signe sort aujourd'hui (le certificat n'est pas achete) : son nom le dit | → test: `TheInstallerCannotShipUnsignedGateTests` |
| L'installeur stage | il est **remplace** entre l'empreinte et le clic, dans un dossier ecrivable par l'utilisateur, puis lance **sous UAC** | le binaire est **verrouille** (partage lecture seule) de l'empreinte jusqu'apres le lancement | → test: `VerificationMiseAJourTests` |
| Le tube nomme privilegie, cote App | un processus du meme compte qui **prend le nom du tube** avant le helper et repond « applique » | l'App compare l'image du serveur au helper pose a cote d'elle, avant le handshake (garde du 11/09/2026) | → test: `TubeUsurpeTests` |
| Le tube, cote App installee | un programme du compte qui pose `DRDXT_HELPER_SESSION_KEY` dans `HKCU\Environment` pour que l'App prenne le client legacy, qui ne verifie pas le serveur | installee sous Program Files, l'App ignore cette cle et passe toujours par la poignee de main (26/09/2026) ; le branchement n'est pas testable hors installation, seule la regle l'est | → test: `ElevatedClientFactoryTests` |
| La disponibilite du helper | un programme du compte qui se **connecte au tube et se tait** (une seule instance) : plus rien a appliquer, plus rien a defaire | 10 s pour parler (poignee de main et requete) ; la connexion muette expire sans arreter le service (26/09/2026) | → test: `HandshakeChannelIntegrationTests` |
| Les baux actifs | un arret du helper (normal, ou provoque par un squatteur du tube) qui laisserait un plafond pose jusqu'au redemarrage | a l'arret, tout bail actif est rendu par le meme chemin que l'expiration (26/09/2026) | → test: `LeaseWatchdogTests` |
| Le tube nomme privilegie, depuis le reseau | une **session reseau** (SMB, meme compte ou administrateur) qui passe l'ACL : .NET ne pose pas `PIPE_REJECT_REMOTE_CLIENTS` (source de `NamedPipeServerStream.Windows.cs`, dotnet/runtime) | refus explicite du SID NETWORK dans l'ACL, evalue avant les autorisations ; la connexion locale reste permise, mesure ; le refus d'un vrai client distant n'est pas mesure faute d'une deuxieme machine (26/09/2026) | → test: `LeTubeDuHelperRefuseLeReseauTests` |
| Le mode du helper installe | `DRDXT_HELPER_SESSION_KEY` posee dans `HKCU\Environment` pour le faire passer en mode legacy, que l'App ne parle plus | installe, le helper ignore cette variable ; regle commune `EstInstalle` (26/09/2026) | → test: `HelperAuthenticatorFactoryTests` |
| Les programmes lances par le helper | un executable du meme nom pose dans le dossier courant, lance **eleve** si le helper l'appelait par son nom seul | tout lancement passe par un chemin absolu vers System32 ; une garde refuse un nom seul | → test: `AucunExecutableLanceParSonNomNuTests` |
| Le helper installe | un programme du compte qui le **lance lui-meme** avec `--app-path` vers son propre executable, sous une UAC au nom de DrDXT | installe sous Program Files, le helper ne croit que les executables de son propre dossier (26/09/2026) | → test: `HelperAuthenticatorFactoryTests` |
| Le rejeu au demarrage | un `pending-revert.json` **piege** (dossier du compte) qui range une ecriture GPU sous un levier de registre | chaque levier ne rejoue que les types d'op qu'il ecrit lui-meme (26/09/2026) | → test: `RevertPiegeAuDemarrageTests` |
| L'annulation BCD | une garde d'annulation **modifiee** qui fait poser un autre element BCD permis | la garde n'est relue que pour `bootstatuspolicy`, le seul element que la carte retire (26/09/2026) | → test: `CartesAgirTests` |
| Les valeurs de registre permises | une ecriture **forgee** vers une feuille permise, avec un autre type ou une autre valeur (un fichier d'echange de 16 Mo, ou pose hors d'un disque) | chaque feuille impose son type, et `PagingFiles` n'accepte que la gestion automatique et le vide que son Annuler remet (26/09/2026). Reste : un rejeu piege nommant le levier du fichier d'echange peut remettre ce vide, l'etat d'origine | → test: `OpAllowlistTests` |
| Le rejeu au demarrage, par feuille | un `pending-revert.json` **piege** qui range sous un levier de registre l'ecriture d'une autre feuille (HAGS portant « `PagingFiles` vide ») | chaque levier ne rejoue que les feuilles que son code ecrit : ruche, chemin et nom ; chaque levier NVIDIA ses seuls reglages DRS (un limiteur d'images range sous « preferer les performances » est refuse) ; chaque levier AMD son seul genre ADLX (la file d'images ne rejoue plus une limite de puissance) (26/09/2026) | → test: `RevertPiegeAuDemarrageTests` |

---

## Ce que cette page ne dit pas

- Elle ne prouve pas qu'il **n'existe pas** d'autre chemin d'attaque. Elle enumere ceux qui sont
  connus et gardes ; **UNKNOWN reste UNKNOWN**.
- Elle ne juge pas la QUALITE de chaque garde. Ce travail-la est ailleurs : les gardes de
  non-nuisance sont prouvees par mutation dans [`NON-NUISANCE.md`](NON-NUISANCE.md), une par une.
- Elle ne couvre pas ce qui n'est pas encore livre : l'installeur pose DrDXT a chaque livraison
  depuis le 03/10/2026, mais **la desinstallation n'a jamais ete verifiee** et **le certificat n'est
  pas achete** : la surface reelle chez un client n'est donc pas entierement observee. Ces deux
  lignes vivent dans `GATES.md`.
- **Residuel connu, sans garde** : un fichier `pending-revert.json` piege peut encore faire rejouer a un
  levier sa propre valeur d'origine, choisie par l'attaquant : une limite de puissance entre 50 et 100 %
  (l'allowlist accepte 0 a 100, l'executeur refuse sous 50, `NvApiTuningRailTests`), un fichier d'echange
  desactive, une limite d'images basse. Depuis le 26/09/2026, il ne peut plus faire rejouer la cible
  d'un autre levier (`RevertPiegeAuDemarrageTests`).
- **La frontiere, dite une fois** : un programme qui tourne sous le compte de l'utilisateur peut ecrire
  ce fichier, mais il peut aussi s'injecter dans le processus de l'App (meme compte, meme niveau
  d'integrite) et demander au helper ce que l'App peut demander. La barriere reelle est donc
  l'allowlist du helper, qui borne tout ce qu'on peut lui faire faire, et non le fichier. Pour cette
  raison, deplacer ce journal dans la racine du helper a ete ecarte le 26/09/2026 : plusieurs jours de
  travail pour une barriere que l'injection contourne.
- Corrige le 26/09/2026 : cette section annoncait l'authentification du tube « a sens unique », defaut
  ouvert. La garde avait ete posee le 11/09 meme ; la ligne vit maintenant dans la table.

## 3. Revue du 08/10/2026 — corrigé, et limites acceptées

Corrigé ce jour :
- Un bail ne dure pas plus de 600 s par renouvellement et un appelant n'en tient pas plus de 8 (`BauxPlafonnesTests`) :
  sans plafond, un appelant gardait le helper administrateur vivant pour toujours.
- Le helper lancé à la demande s'arrête après 15 minutes sans connexion authentifiée et sans bail
  (`ArretSurInactiviteTests`) ; une connexion refusée ne le garde plus en vie.
- Les valeurs de la session (HKCU) s'écrivent avec le jeton du joueur, après la même allowlist
  (`ClientSessionUtilisateurTests`).
- Le mode legacy à clé pré-partagée n'existe plus dans un build Release.
- Le fichier temporaire de l'état du service est supprimé s'il existe, puis créé en exclusif.

Limites acceptées, dites :
- Le helper fait confiance à l'image de l'App (chemin + SHA-256), qui tourne en intégrité moyenne : un programme du
  même compte qui s'injecte dans l'App peut, tant que le helper vit, lancer les ops de l'allowlist. L'allowlist borne
  tout (aucune exécution de code arbitraire) ; l'arrêt pour inactivité réduit la fenêtre.
- La désinstallation, lancée élevée, rend aussi les valeurs HKCU gardées : avec un UAC donné par un autre compte, elle
  écrirait la ruche de ce compte. Les refuser au helper empêcherait de les rendre dans le cas courant.
- ~~Côté AMD, la fréquence maximale est bornée par la plage du pilote, pas par une marge propre au helper.~~ Plus vrai
  depuis le 17/10/2026 : le helper la tient dans sa propre bande (section 5).
- Aucun compteur anti-rejeu dans le manifeste signé : rejouer un ancien manifeste demande d'écrire dans un dossier
  réservé aux administrateurs.
- Un programme du compte peut squatter le nom du tube entre deux connexions : déni de service, l'App refuse le squatteur.

## 4. Revue du 10/10/2026 — le programme qui reçoit l'élévation est natif

Trouvé par une revue adverse indépendante : un processus élevé par l'UAC reçoit son environnement de
`HKCU\Environment`, que n'importe quel programme du compte écrit sans droits. Le runtime .NET y lit, avant la première
ligne du programme, de quoi charger une DLL de son choix (`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER_PATH`,
documentées par Microsoft ; `StartupHookSupport=false` ne coupe que `DOTNET_STARTUP_HOOKS`). Chaque « Oui » donné à
DrDXT aurait fait tourner ce code en administrateur, dans l'image authentique du helper.

Corrigé : le seul exécutable de DrDXT qui reçoit l'élévation est `DrDxt.Lanceur.exe`, compilé en natif (NativeAOT, sans
CoreCLR : ces variables n'ont aucune prise). Il ne démarre que les programmes de DrDXT, à côté de lui, avec un environnement
reconstruit depuis les API Windows (`EnvironnementPropre` : SystemRoot, PATH limité à Windows, dossiers du profil lus au
jeton) : rien de l'environnement reçu ne passe. Le helper (`RunAsProcessLauncher.Cible`), l'App qui se relance élevée
(`RelanceElevee`) et le zip de validation passent par lui ; l'installeur le livre (`LeLanceurNatifRecoitLElevationTests`).
Mesuré le 10/10 : lancé par le lanceur avec `CORECLR_*` et `DOTNET_*` posés, le programme fils ne voit aucune de ces
variables (13 variables, PATH = Windows seul).

Aussi corrigé : `StartupHookSupport=false` sur le service ; le téléchargement d'une mise à jour est borné à 400 Mo.

Limites redites par cette revue, acceptées :
- Ce qu'un programme du compte obtient à travers l'allowlist, sans exécution de code : fichier d'échange vide
  (plantages jusqu'à remise à la main), redémarrage dans le BIOS, options BCD, plafond d'envoi. Réversible, borné.
- Le helper ne doit jamais être diffusé hors installeur : hors Program Files, il fait confiance à l'image passée en
  `--app-path`, et le compte peut le remplacer avant l'élévation. Seul l'installeur est publié.

## 5. Audit du 17/10/2026 — « DrDXT ne fait jamais de mal »

Une lecture de tout ce qui écrit sur la machine, contre un programme hostile du compte du joueur (qui peut s'injecter dans
l'App et parler au helper, section 3 : seule l'allowlist et les rails de l'exécuteur le bornent).

Corrigé :
- **PowerShell élevé, modules du compte.** PowerShell 5.1 met `Documents\WindowsPowerShell\Modules` en tête de
  `PSModulePath`, même élevé, et le compte y écrit sans droits (un dossier OneDrive s'y synchronise même depuis un autre
  appareil). Mesuré sur la machine de référence : un module témoin qui y exporte `Checkpoint-Computer` passait avant celui
  de Windows ; le helper l'aurait exécuté en administrateur, sans invite, avant chaque lot. Le point de restauration reçoit
  un `PSModulePath` réduit aux modules de Windows, importe le module par son chemin System32 et s'arrête si la commande vient
  d'ailleurs (`ModuleCheckpointPiegeTests`) ; le lanceur fixe le même chemin pour la console de réparation et tout
  programme qu'il élève (`EnvironnementPropreTests`).
- **Annulations lues dans un fichier du compte.** « Annuler », le retrait automatique, l'essai et la surveillance de la carte
  rejouaient par le helper l'op lue dans `leviers-appliques.json`, `reglage-carte-retenu.json` ou `preuve-repos-carte.json`,
  sans filtre : une écriture de fichier faisait exécuter n'importe quelle op de l'allowlist au premier clic. Le contrôle du
  démarrage (types et feuilles écrits par chaque levier) passe à la lecture des trois fichiers
  (`AnnulationsLuesDansUnFichierTests`).
- **Limite de puissance du processeur.** Une montée n'était bornée qu'à la PL2 du registre, souvent 0x7FFF (4095 W) : elle
  est bornée à la puissance maximale publiée (0x614), et refusée sous une PL2 « sans limite » sans maximum publié. Éteindre
  la PL1 par son bit d'activation (le processeur tenait alors la PL2 sans fin) est refusé, sauf pour rendre exactement le
  firmware de ce démarrage (`PawnIoCpuPowerActionsTests`).
- **Sous-tension AMD.** La MaxFreq reste dans la bande prudente de l'App autour de la valeur du démarrage, en plus de la plage
  du pilote, et elle est relue. Rendre au-delà de la bande (une sous-tension gardée au redémarrage) n'écrit jamais la
  fréquence demandée et ne remet rien à l'usine d'elle-même : le helper le refuse et le dit. La remise à l'usine est un
  geste à part, jamais le retour d'un bail ni rejouée au démarrage (`ADLXTuningActionsTests`, `LeaseWatchdogTests`,
  `RevertPiegeAuDemarrageTests`) ; un programme du compte peut l'envoyer lui-même, jamais aller au-delà du défaut du pilote.
- **Désinstallation par le lanceur.** `[UninstallRun]` lançait le helper .NET directement, élevé, avec l'environnement du
  compte (`CORECLR_PROFILER`…) : il passe par `DrDxt.Lanceur.exe`, qui attend sa fin dans ce mode ; le PowerShell du point
  de restauration retire aussi les variables du runtime (`DesinstallationInstalleurTests`, `EnvironnementPropreTests`).
- **Rapport de vérification** : plus rien du rapport ni du matériel dans l'adresse du ticket, que le navigateur envoie à
  GitHub dès l'ouverture (`LienDeRapportTests`).
- **Type de registre** : une origine REG_SZ, REG_QWORD ou REG_EXPAND_SZ n'est plus prise pour un DWORD ou un texte, que
  l'annulation aurait réécrits dans un autre type (`TypeDuRegistreConserveTests`).
- **Service de démarrage** : la passe quotidienne attend la fin d'une partie (`WorkerTests`).
- **Origine du BCD retiré.** `GardeDuBcdRetire` l'écrit et la force sur le disque avant bcdedit, et l'efface si le helper
  refuse ; l'écran passe par elle depuis 70a4fd8a, et une garde impossible n'ôte rien (`GardeDuBcdRetireTests`).
- **Mise à jour.** Le démarrage ne lit plus que le manifeste signé (`AutoUpdater.AnnoncerAsync`) ; l'installeur ne se
  télécharge qu'au clic « Installer » (`TelechargerAsync`), branché depuis e3d5d3a5 (`AutoUpdaterTests`, `MiseAJourLenteTests`).
- **Battement d'un bail sans invite.** Le battement ne lance jamais le helper (`IElevatedClient.SansRelance`) : refusée, une
  invite Windows revenait à chaque battement, en jeu compris (`BattementSansInviteTests`). Helper injoignable, le ventilateur
  fixe est rendu à l'automatique par une seule tentative, et l'écran dit s'il est resté fixe (`EssaiRenduLibereLeBailTests`).
- **Remise à l'usine AMD sur clic.** `AnnulationParClic` envoie la remise à l'usine sur « Annuler », ou sur le bouton du
  constat de démarrage, quand le helper la demande ; une remise réussie vaut « rendu » (`AnnulationParClicTests`). Branchée
  dans l'écran depuis 23118e76 : seul le clic la déclenche. Le démarrage et la garde d'incident, qui rendent sans clic
  (`AnnulerLevierAsync(id, parClic: false)`), disent le refus du helper et ne remettent jamais la carte à l'usine.

Corrigé — **la tâche planifiée « DrDXT releve »** (limite R-H, puis H-1 du second audit). Elle tournait au niveau le plus
élevé (`/RL HIGHEST`, pour lire la température du processeur par PawnIO) : l'App élevée écrivait alors dans
`%LOCALAPPDATA%\DrDXT`, où un programme sans droits du même compte peut poser une jonction, et chaque ouverture de session
devenait une création, un écrasement ou une suppression de fichier en administrateur ; elle faisait aussi avancer l'essai en
alternance sans invite. La tâche tourne désormais sans droits (`/RL LIMITED`), lance l'App directement (le lanceur, non
élevé, demanderait l'élévation), ne lance jamais le helper (`CreateSansLancer`) et ne pose ni ne rend aucun levier. Une mise
à jour remplace la tâche d'une installation précédente (`/Create /F`). Ce qu'elle perd : sans le service DrDXT, une partie
jouée DrDXT fermé n'a ni température ni état du processeur, et le repli `logman` du compteur d'images (une session ETW,
réservée aux administrateurs) ne s'ouvre plus en fond (`ReleveSansLAppTests`).

Corrigé — **journaux d'annulation vidés sur le disque** (M-4 du second audit). `FichierJsonAtomique` écrit son temporaire
en WriteThrough et le vide avant le renommage : une coupure ne laisse plus un `leviers-appliques.json` illisible
(`FichierJsonAtomiqueTests`). Un journal illisible, par coupure ou parce qu'un programme du compte l'a écrit, n'arrête plus
le journal en silence : celui des leviers posés est dit au scan puis mis de côté au réglage suivant, celui de la session est
dit au démarrage puis mis de côté au lieu d'être écrasé ou supprimé (`JournalDesLeviersAppliquesTests`,
`RevertEnAttenteIllisibleTests`). Limite connue : Optimiser garde l'origine d'un levier après l'avoir posé (le snapshot naît
de la pose elle-même, `TweakEngine`) ; une coupure entre les deux laisse le levier posé sans « Annuler ». Le seul cas sans
retour par Windows, le BCD, est gardé avant l'écriture (`GardeDuBcdRetire`).

Corrigé en partie, résidu accepté — **l'essai en alternance lit un fichier du compte** (M-2 du second audit).
`essai-alternance.json` vit sous le profil, que tout programme du compte peut écrire. « Arrêter l'essai » ne pose ni ne rend
plus un levier qui n'a pas le droit d'alterner, et l'essai ne pose rien sans clic pendant qu'un jeu tourne
(`EssaiEnAlternanceTests`). Résidu : un fichier forgé peut faire poser sans clic, entre deux parties, un levier à chaud
(AutoHot), dans ses bornes. La frontière reste l'allowlist du helper et ses bornes : un programme du compte peut de toute
façon piloter l'App. Seule la pose est bloquée pendant un jeu : rendre le levier, vers son origine, reste permis sans clic
pendant qu'un jeu tourne, ce qui est aussi une écriture.

Corrigé, non validé sur matériel — **le cliquet ADLX d'un démarrage à l'autre** (M-1 du second audit). La bande de
+100/−200 MHz se mesurait autour de la MaxFreq lue au premier passage de chaque démarrage : si le pilote garde la valeur au
redémarrage, +100 MHz par démarrage jusqu'au plafond d'overclock. L'origine est désormais gardée par carte dans
`HKLM\SOFTWARE\DrDXT\Helper\AdlxOrigine` (écrite par le helper seul, retirée à la désinstallation), avec la dernière valeur
écrite par DrDXT, notée avant chaque écriture : au démarrage suivant, une MaxFreq égale à cette valeur garde l'origine, une
autre (réglée dans AMD Software, ou remise à l'usine) devient l'origine (`AdlxOrigineEntreDemarragesTests`). Limites :
l'identité est l'index ADLX et les identifiants PNP des cartes AMD (une carte ajoutée ou retirée fait repartir de la valeur
lue) ; et si ADLX accepte cette écriture sans élévation, comme AMD Software, le helper n'est pas la frontière pour AMD.
Quand l'ensemble des cartes AMD vues change (puce intégrée activée, carte retirée), l'origine repart de la valeur lue, que
le pilote peut porter au-dessus du défaut ; changer cet ensemble demande un geste matériel ou des droits.

Corrigé — **le fichier d'échange coupé par un appelant compromis** (M-5, point 1, du second audit). L'allowlist admettait un
`PagingFiles` vide (l'Annuler de `fixit.sane_pagefile` rend une origine vide) et n'importe quelle lettre de lecteur : au
redémarrage suivant, plus de fichier d'échange. L'écrivain du registre du helper, par lequel passent les trois exécutants,
n'écrit plus le vide que si la valeur actuelle est déjà vide ou si le helper a noté, en écrivant par-dessus, que l'origine
l'était ; la note garde la valeur que DrDXT a posée, et le vide n'est accepté que si cette valeur est encore en place (le
joueur a réglé son fichier d'échange depuis : vide refusé, note retirée) ; elle survit au redémarrage et s'efface au rendu.
Résidu : tant que le réglage de DrDXT reste en place, rendre le vide d'origine reste possible, comme l'Annuler du joueur.
Une entrée sur un lecteur nommé doit viser un lecteur fixe qui existe (`GardeDuFichierDEchangeTests`).

Corrigé, résidus dits — **l'App relancée en administrateur sous un dossier du compte** (résidu de la revue de secu-2).
« Relancer en administrateur » et `--analyser` rendent l'App élevée, après un clic et un « Oui » ; elle écrit, renomme et
supprime sous `%LOCALAPPDATA%\DrDXT`, que tout programme du compte peut modifier. Une jonction posée d'avance (vers
`\RPC Control` et des liens objet, ou vers un dossier de Windows) faisait de ce « Oui » une écriture ou une suppression en
administrateur ailleurs. Au démarrage élevé, avant toute écriture, l'App crée les sous-dossiers qu'elle utilise
(`diagnostics`, `logs`), vérifie sans suivre les liens que ni le dossier, ni un sous-dossier, ni un fichier n'est un point de
reparse ou un lien dur, puis tient le dossier et ses sous-dossiers ouverts sans partage de suppression jusqu'à sa fermeture :
aucun ne peut être renommé ni remplacé par une jonction. Un lien trouvé : elle ne démarre pas et le dit
(`DossierDeLAppEleveTests`, sur de vraies jonctions et de vrais liens durs). Résidus, après le contrôle : un lien symbolique
de fichier, que Windows ne laisse créer sans élévation qu'avec le mode développeur activé (Microsoft Learn,
`CreateSymbolicLinkW`, `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`) ; un fichier remplacé par un lien dur, pour lequel
Microsoft ne documente aucune exigence d'accès sur le fichier visé (`CreateHardLinkW`) : une écriture directe de l'App
élevée sur ce nom irait dans ce fichier. Les écritures atomiques (temporaire puis renommage) remplacent l'entrée sans
écrire à travers le lien. La machine exportée et le rapport de « Vérifier DrDXT » vont sous Documents, hors du dossier
tenu : élevée, l'App refuse une jonction ou un lien symbolique sur Documents et chaque parent jusqu'au profil (étiquette de
reparse lue sans suivre le lien ; l'étiquette de fichiers cloud de OneDrive, relevée sur la machine de référence, est
admise), et crée le fichier sans jamais ouvrir un nom qui existe déjà ; un nom déjà pris est dit, rien n'est supprimé.
L'étiquette est lue sur des handles ouverts sans partage de suppression, tenus jusqu'à la fin de la création : entre le
contrôle et l'écriture, aucun de ces dossiers ne peut être renommé ni remplacé par une jonction (`EcritureDansDocumentsTests`).

Corrigé, résidu dit — **le plafond d'envoi retenu pour le service SYSTEM** (M-5, point 2, du second audit). Retenu, il est
posé par le service à chaque session, sans DrDXT ouvert et sans invite, dès que l'exe nommé tourne. Le helper n'exigeait
qu'un nom de jeu possible : un programme qui donnait son propre nom obtenait le bridage de tout l'envoi de la machine et sa
propre exemption, redémarrages compris. Seul un jeu reconnu par son nom (`NomExeDeJeu.EstUnJeuConnu`, liste fermée des jeux
compétitifs les plus joués) est désormais retenu, et le service ne rejoue plus un plafond retenu pour un autre nom ; un
autre jeu garde la protection de l'App, sous bail, tant qu'elle est ouverte (`PlafondRetenuPourLeServiceTests`,
`PlafondEnvoiDuServiceTests`, `CartesAgirTests`). Résidu : un programme qui se nomme comme un jeu reconnu (`cod.exe`)
obtient encore le bridage pendant qu'il tourne. Pas de plancher de débit : un envoi ADSL légitime peut être sous quelques
Mbit/s. La reconnaissance se fait par le nom seulement, ni chemin ni signature.

Corrigé — **le ventilateur fixe sans bail** (M-5, point 3, du second audit). `ApplyGpuFanCurveOp` passait sans bail ; la garde
thermique de 80 °C et l'expiration ne voient que les baux : une vitesse fixe forgée restait jusqu'au redémarrage pendant que
la carte chauffait, et la promesse « jamais une vitesse fixe au-dessus de 80 °C » était fausse pour elle. Le serveur du
helper ne la pose plus que sous un bail non échu du même appelant dont le retour rend ce ventilateur à l'automatique
(`LeaseIpcPipelineTests`).

Corrigé — **le limiteur d'images au profil de base** (M-5, point 4, du second audit). La liste blanche admettait au profil de
base toute valeur documentée par NVIDIA, dont un limiteur à 1 i/s, et l'écriture globale n'était pas relue. Au profil de
base, un limiteur sous 20 i/s est refusé (règle partagée avec les leviers, `ValeursDrs.Admise`), et l'écriture globale est
relue en session neuve : une valeur que le pilote n'a pas gardée est un échec (`ValeursDrsBorneesTests`,
`NvApiDrsActionsTests`). La synchronisation verticale forcée au profil de base reste admise (réversible dans le panneau
NVIDIA).

Corrigé — **un antivirus ou un contrôle parental coupé au démarrage** (M-5, point 4). Seule la sécurité de Windows était
protégée dans StartupApproved : un appelant compromis pouvait couper l'interface d'un antivirus tiers ou un contrôle
parental. Une règle partagée (`ProtegesAuDemarrage`, fragments de nom en liste fermée) les protège dans la liste blanche du
helper, le constat et les gestes du docteur ; les réactiver reste permis (`StartupApprovalAllowlistTests`,
`ProgrammesAuDemarrageTests`). Un logiciel de sécurité absent de la liste peut encore être coupé.

Choix de produit, dits — **ce que les bornes laissent encore** (M-5, point 4). Un appelant compromis du helper obtient,
dans les bornes : −125 mV sur le processeur (la borne absolue tient, mais la plupart des Raptor Lake plantent à cette
valeur, jusqu'au redémarrage : la tension est volatile) ; une PL1 jusqu'à min(PL2, plafond du registre 0x614) sur un
microcode à jour (100 °C tenus sur la machine de référence, PROCHOT protège le silicium) ; trois éléments BCD fermés
(`useplatformclock`, `tscsyncpolicy`, `bootstatuspolicy`), dont l'effet sous BitLocker (récupération demandée au démarrage
suivant ?) n'est pas mesuré (GATES). Ces valeurs sont des leviers du produit : les retirer retirerait les leviers.

## 6. Guide BIOS au téléphone — limite acceptée

Le QR du guide BIOS ouvre `docs/guide-bios/index.html` (GitHub Pages), le guide dans le fragment de l'adresse, que le
navigateur n'envoie jamais. N'importe qui peut forger une telle adresse et la poster sous forme de QR.

**La page ne peut pas prouver qu'un QR vient de DrDXT ; elle n'affiche que des phrases fixes et des valeurs de menu bornées,
sous un avertissement.** Chaque consigne, ligne de secours ou note est un code d'une liste fermée dont les phrases sont dans
la page (aucune ne parle de tension) ; le nom de la carte, les touches et les valeurs suivent des règles de longueur et de
caractères, sans adresse ni nombre suivi d'une unité de tension (V, mV, volt, volts, quelle que soit la casse) ; aucune ligne ne dit « vérifié » ; une ligne fixe en tête dit de ne
suivre que le QR scanné depuis DrDXT. L'App applique les mêmes règles et garde le QR en texte pour un guide qu'elles
refuseraient (`GuideBiosParLaPageTests`). Il n'y a pas de signature : une clé dans l'App serait publique.

Ce qui passe quand même : une valeur numérique sans unité, par exemple « CPU Core Voltage → Override = 1.550 », sous une
consigne fixe comme « Active ton profil mémoire (XMP/EXPO) ici ».

## 7. Identifiants Windows et proxy — limite acceptée

Les clients HTTP de l'App (vérification de mise à jour au démarrage, tables signées, test de la ligne) passaient les
identifiants du compte à tout proxy qui répondait 407. Sur un réseau hostile (Wi-Fi public, LAN d'événement), un faux
proxy publié par WPAD recevait ainsi l'échange NTLM du compte à chaque lancement, sans clic : de quoi le relayer ou le
casser hors ligne. Attendre un 407 n'y change rien, puisqu'un faux proxy en répond un.

**Les identifiants ne partent que vers un proxy que quelqu'un a configuré** : une variable HTTP_PROXY, HTTPS_PROXY ou
ALL_PROXY valide (.NET n'utilise alors plus le proxy du système), ou un proxy manuel quand ni la détection automatique ni
un script PAC ne sont actifs. Avec la détection ou un script, le proxy se choisit hôte par hôte (le téléchargement est
redirigé vers un autre hôte que le manifeste) : la décision porte donc sur la configuration, pas sur une adresse. Une
configuration illisible ne donne rien (`ProxyDuCompteTests`, avec un faux proxy local qui exige NTLM).

Limite acceptée : derrière un proxy d'entreprise par détection automatique (WPAD) ou par script PAC qui exige
l'authentification, et derrière un proxy manuel quand la case « Détecter automatiquement les paramètres » reste cochée,
la vérification de mise à jour reste « injoignable ». Décocher la détection, ou poser HTTPS_PROXY, la rend.

## 8. Manifestes sans date — limite acceptée

Les manifestes signés (mise à jour de l'App, tables signées) ne portent ni date de publication ni expiration. Un
miroir compromis ou un attaquant réseau, incapable de signer, peut resservir un ancien manifeste valide, ou répondre 404 :
l'App dit alors « à jour » et le joueur reste sur sa version sans le savoir. **C'est un gel possible, jamais un retour
arrière** : une version plus ancienne que celle installée est refusée (`UPDATE_DOWNGRADE`), et des tables plus anciennes
que celles en service aussi. La parade (manifestes datés, re-signés au moins tous les 30 jours) imposerait une
re-signature mensuelle avec la clé de release ; elle est décrite dans GATES.md (§7), en attente d'une CI qui tienne la clé.

## 9. DLL posées à côté de l'installeur — constat examiné

Le bouton « Installer » copie l'installeur vérifié dans le dossier de préparation, sous `%LOCALAPPDATA%` (écrivable par le
compte), le verrouille en lecture et le lance par le shell. Le verrou protège l'exe, pas ses voisins : la question était de
savoir si une DLL posée à côté serait chargée en administrateur.

**Vérifié** (sans lancer, sur le setup 0.9.1, Inno Setup 6.7) :
- le manifeste du setup demande `asInvoker` : `DrDXT-Setup-*.exe` tourne sans élévation. Une DLL posée dans le dossier de
  préparation serait chargée avec les droits du compte, ceux qu'il fallait déjà pour l'y poser : aucun gain. Le manifeste
  déclare un niveau, donc la détection heuristique des installeurs par Windows ne s'applique pas ;
- ses imports statiques sont advapi32, comctl32, kernel32, oleaut32 et user32 ;
- la partie élevée est `setup.tmp`, qu'Inno extrait dans son propre dossier temporaire (`is-` suivi de 10 caractères tirés
  au hasard cryptographique) puis relance sous UAC, pas depuis le dossier de préparation. Inno 6.7 annonce des protections
  supplémentaires contre le préchargement de DLL.

**Non vérifié** : la recherche réelle de DLL de `setup.tmp` élevé. Il faut Procmon pendant une vraie installation, donc un
clic UAC : le geste est dans GATES.md §1.

**Résidu** : le dossier temporaire d'Inno est sous le `%TEMP%` du compte. Un code qui tourne déjà sous ce compte peut viser
`setup.tmp`, comme pour tout installeur Inno ; Microsoft ne traite pas l'UAC d'un même administrateur comme une frontière
de sécurité.

**Fermé : l'App déjà élevée.** Relancée en administrateur, l'App aurait téléchargé et préparé l'installeur en
administrateur dans le dossier du compte (une jonction posée d'avance y redirige l'écriture), puis passé son jeton élevé au
setup, qui aurait chargé ce qui est posé à côté de lui. Dans une App élevée, le bouton « Installer » se tait : rien ne se
télécharge, la version reste nommée avec « Relance DrDXT normalement pour installer la mise à jour »
(`Une_App_elevee_ne_telecharge_rien_et_dit_de_relancer_normalement`). Le lancement le refuse aussi, avant toute copie,
sans retirer la version (`Une_App_elevee_ne_lance_pas_l_installeur`). Conséquence dite : sur un PC où l'UAC est désactivé, tout
processus d'un administrateur est élevé, et le bouton refuse toujours ; l'installeur se télécharge alors à la main.

## 10. Sous-tension AMD à la désinstallation — limite connue, non validée sur AMD

La désinstallation rend ce que le manifeste signé porte, et remet une carte NVIDIA au ventilateur automatique et au
décalage d'horloge 0 (`RendreLaCarteNvidiaAsync`). Une sous-tension AMD (`ApplyAdlxTuningOp` de type `Undervolt`)
n'entre pas au manifeste : son origine n'est pas de celles que la désinstallation sait rendre (`OrigineRendable`).
Si le pilote AMD la garde après un redémarrage, elle reste posée après la désinstallation, jusqu'à une remise à zéro dans
le logiciel AMD. Le comportement réel au redémarrage n'a pas été mesuré, faute de carte AMD. Un essai en alternance ne
la pose jamais : une origine ni rendable ni remise à l'automatique par la désinstallation est rendue aussitôt
(`Un_levier_que_la_desinstallation_ne_rendrait_pas_n_est_jamais_essaye`).
