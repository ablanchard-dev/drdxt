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

**TEST-ONLY.**

- **Ou** : 17 projets, **tous des projets `.Tests`** ; **aucun projet de production**.
- **Trace mesuree** : `xunit 2.5.3` -> `xunit.assert` / `xunit.core` -> `NETStandard.Library 1.6.1`
  -> `System.Net.Http 4.3.0`. C'est le harnais de test qui le tire, pas le produit.
- **Verdict** : le code du produit livre au client ne contient pas ce paquet. Il ne s'execute que
  sous `dotnet test`, sur cette machine et dans la CI. Sur `net8.0`, ce paquet n'est de toute facon
  qu'une **facade** : l'implementation utilisee est celle du framework.
- **Residuel dit** : un poste de developpement compromis reste un poste compromis — cette CVE n'y
  change rien, et elle ne la rend ni plus ni moins probable.

### GHSA-cmhx-cq75-c4mj — `System.Text.RegularExpressions` 4.3.0

**TEST-ONLY.** Meme trace, meme chaine (`xunit` -> `NETStandard.Library 1.6.1`), memes 17 projets de
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
| La machine du client | un **19e op** ajoute par megarde plus tard | surface figee a 18 noms | → test: `ThePrivilegedSurfaceIsClosedGateTests` |
| Le materiel | une **ecriture de tension** ajoutee plus tard | elle **n'existe pas**, et le filtre de noms sait attraper un coupable fabrique | → test: `NoVoltageWriteOpExistsGateTests` |
| Le BIOS | le composant qui **guide** dans le BIOS, s'il ecrivait | `DrDxt.Bios` ne reference que `DrDxt.Core` : structurellement incapable | → test: `TheGuideNeverWritesTheBiosGateTests` |
| L'etat desire du reconciliateur | un fichier **altere** sur le disque | signature verifiee, chargement **fail-closed** | → test: `SignedDesiredStateStoreTests` |
| Le rapport SAV | ce que le client **colle** dans un ticket public | masquage des chemins utilisateur et des secrets | → test: `TextScrubberTests` |
| L'installeur | livrer un binaire **non signe** | gate de publication | → test: `TheInstallerCannotShipUnsignedGateTests` |
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
- Elle ne couvre pas ce qui n'est pas encore livre : tant que **l'installeur n'a jamais ete
  execute** et que **le certificat n'est pas achete**, la surface reelle chez un client n'a jamais
  ete observee. Ces deux lignes vivent dans `GATES.md`.
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
- Côté AMD, la fréquence maximale est bornée par la plage du pilote, pas par une marge propre au helper.
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
