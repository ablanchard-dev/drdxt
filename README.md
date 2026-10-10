# DrDXT

English version: [README.en.md](README.en.md).

Un optimiseur PC gaming pour Windows. Gratuit, libre, et honnête : il ne promet rien qu'il n'ait mesuré.

DrDXT lit ta machine, te dit ce qui te coûte des images, et pose seulement des réglages qu'il sait défaire. Chaque
réglage est jugé sur tes vraies parties : gardé s'il apporte quelque chose, rendu sinon. Si ta machine est déjà bien
réglée, il te le dit aussi.

## Ce qu'il fait

- **Un scan** lit ton processeur, ta carte graphique, ta mémoire, ton stockage, Windows, le réseau et le BIOS. Rien n'est
  écrit à ce stade.
- **Un docteur par domaine** explique chaque point : ce qui a été lu, ce qui pose problème, ce que DrDXT peut faire, et
  ce qui se fait à la main (BIOS, câbles, réglages du jeu), étape par étape.
- **Optimiser** pose les réglages utiles, un par un, après un point de restauration. **Essayer en alternance** compare
  un réglage sur tes parties, une sur deux, et tranche au bout de six.
- **Tes parties** sont relevées en arrière-plan : images par seconde, 1 % les plus lentes, ce qui limitait, programmes
  ouverts à côté. C'est la seule preuve que DrDXT accepte.
- **Le mode Expert** ouvre la fréquence et le ventilateur de la carte graphique, sur ton accord, avec test de stabilité
  et retour automatique.
- **Vérifier DrDXT sur mon PC** pose chaque réglage, le relit, le rend et le relit encore, puis écrit un rapport sans
  rien qui te désigne. C'est ainsi que ton matériel entre dans ce que DrDXT sait prouver.

## Où en est le projet

Version 0.9, utilisable aujourd'hui. Le scan, le docteur, le relevé de tes parties, Optimiser, l'essai en alternance et
le retour arrière de chaque réglage tournent sur de vrais PC, et chaque réglage a été posé, relu et rendu sur une vraie
machine avant d'être proposé.

Des joueurs ont déjà utilisé une version dérivée d'une partie de DrDXT, préparée par un proche du projet. Certains
rapportent une latence stable autour de 10 ms, d'autres 30 à 50 images par seconde de plus. Ces chiffres viennent d'eux :
DrDXT ne les a pas encore mesurés lui-même, et on ne sait pas encore ce que cette version fait de plus. C'est exactement
ce que l'essai en alternance et les rapports de vérification servent à établir, PC par PC.

Ce qui reste à prouver : un gain mesuré par DrDXT sur des parties, et les cartes AMD, Intel Arc, les portables à deux
cartes et les consoles portables, prévus dans le code mais testés seulement sur des machines reconstituées.
[docs/COUVERTURE.md](docs/COUVERTURE.md) dit, réglage par réglage, ce qui est prouvé, à prouver, ou absent de ton matériel.

## Installer

1. Télécharge `DrDXT-Setup-NON-SIGNE.exe` dans la dernière [Release](https://github.com/ablanchard-dev/drdxt/releases).
   Son empreinte SHA-256 est dans la page ; `Get-FileHash` dans PowerShell la recalcule.
2. L'installeur n'est pas encore signé : Windows affiche « Windows a protégé votre ordinateur ». Clique « Informations
   complémentaires », puis « Exécuter quand même ».
3. Deux cases : un raccourci sur le Bureau, et le service `DrDxtReconciler`, décoché par défaut, qui réapplique au
   démarrage de Windows les réglages que tu as gardés.
4. Au premier lancement, « Lancer le scan » lit la machine sans rien écrire. Rien n'est posé avant que tu cliques
   « Optimiser » ou « Essayer en alternance », et chaque réglage se défait depuis « Ce que DrDXT a posé ».
5. Désinstaller : Paramètres, Applications, DrDXT. Le désinstalleur rend d'abord les réglages qu'il connaît, puis retire
   les fichiers. Restent `%LOCALAPPDATA%\DrDXT` (tes relevés) et `%ProgramData%\DrDxt` (ses rapports), à supprimer à la
   main si tu veux repartir de zéro.

Windows 10 ou 11, 64 bits, sur un **compte administrateur** : c'est lui qui donne son accord à chaque réglage. Sur un
compte standard, Windows demanderait le mot de passe d'un administrateur et les réglages iraient dans son profil : ce
n'est pas prévu. Aucun .NET à installer, tout est dans l'installeur.

## Participer

L'aide la plus utile ne demande pas de savoir coder :

- **Vérifier DrDXT sur ton PC**, puis glisser le rapport dans un ticket « Rapport de vérification ».
- **Sans installer DrDXT** : les zips de la [Release](https://github.com/ablanchard-dev/drdxt/releases) lisent ton PC,
  ou font la même vérification, et écrivent le même rapport.
- **Signaler un bug**, ou proposer du code : [CONTRIBUTING.md](CONTRIBUTING.md). Une faille de sécurité se signale
  en privé : [SECURITY.md](SECURITY.md).

## Ce que DrDXT écrit sur ta machine

Tout réglage passe par un programme élevé séparé, le helper, qui refuse ce qui n'est pas dans sa liste. Chaque écriture
lit d'abord la valeur d'origine et sait la rendre. La liste, en entier :

- **Registre, valeurs exactes**, jamais une branche entière : `HwSchMode` (planification GPU accélérée),
  `PagingFiles` (fichier d'échange désactivé, remis), `AllowAutoGameMode` et `AutoGameModeEnabled` (mode Jeu),
  `HistoricalCaptureEnabled` (enregistrement en arrière-plan de la Game Bar), `DirectXUserGlobalSettings` (VRR et
  modèle d'affichage), `MouseSpeed`, `MouseThreshold1` et `MouseThreshold2` (accélération du pointeur). Deux valeurs
  ne sont plus posées, seulement rendues si une ancienne version les a écrites : `LowLatencyMode` et `CppcEnable`.
- **Pilote NVIDIA**, au profil du jeu seulement : file de rendu, synchronisation verticale, limiteur d'images, mode
  d'alimentation.
- **Plan d'alimentation** : un plan Windows connu, et le boost du processeur dans ce plan.
- **Un point de restauration** avant d'appliquer ; la restauration système est activée sur le disque système si elle
  ne l'était pas. Deux points DrDXT au plus, réutilisés sous 24 heures.
- **Deux services** : PresentMon, qui mesure les images, et le service AMD 3D V-Cache.
- **Carte graphique**, dans des bornes : la limite de puissance, essayée en jeu et gardée seulement avec un gain
  mesuré ; en mode Expert, sur ton accord, le décalage d'horloge et une vitesse fixe du ventilateur, rendus seuls si
  DrDXT s'arrête.
- **Deux règles QoS** pendant une partie, sur ta demande, pour plafonner l'envoi ; retirées à la fin de la partie.
- **Démarrage de Windows**, cinq éléments exacts, sur ta demande, pour retirer un réglage posé par un script.
- **Redémarrer vers le BIOS**, sur ta demande. Le BIOS est lu, jamais écrit.
- **Le manifeste signé** des réglages gardés, dans `%ProgramData%\DrDxt`, que la désinstallation relit pour les
  rendre.

Jamais écrit, quoi qu'on demande : Windows Defender, Windows Update, les services de Windows, le firmware. Le détail,
site par site, est dans [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md) ; la sécurité du helper et ce qui est accepté
comme limite, dans [docs/SECURITE.md](docs/SECURITE.md).

## Ce qui quitte la machine

Pas de télémétrie, pas de compte. Voici tout ce que DrDXT envoie sur le réseau. Ce qu'il lit et garde sur ta machine est
dans [CONFIDENTIALITE.md](CONFIDENTIALITE.md).

- **La dernière version des pilotes graphiques**, à chaque scan, pour te dire si le tien a du retard : la page publique
  d'AMD (`www.amd.com`) et une requête fixe au service public de NVIDIA (`gfwsl.geforce.com`, toujours la même,
  interrogé seulement sur une carte GeForce). Rien ne te désigne.
- **Des pings** vers ta box et vers `1.1.1.1`, pour mesurer la latence ; pendant une partie, aussi vers le serveur du
  jeu, pour suivre la route d'une partie à l'autre.
- **La vérification de mise à jour**, au démarrage : deux petits fichiers lus sur `https://ablanchard-dev.github.io/drdxt`.
  GitHub voit ton adresse IP, comme pour toute page web. Pour la couper : la variable d'environnement `DRDXT_UPDATE_URL`
  posée à `off`. Une mise à jour ne s'installe jamais sans ton clic ([docs/MISE-A-JOUR.md](docs/MISE-A-JOUR.md)).
- **La page support de ta carte mère MSI**, seulement si tu cliques « Ouvrir la page de ta carte » dans le guide du
  BIOS : ton navigateur ouvre `www.msi.com/Motherboard/<modèle>/support`.
- **Ton rapport de vérification**, seulement si tu cliques « Envoyer mon rapport » : DrDXT ouvre ton navigateur sur un
  ticket GitHub déjà rempli. Rien ne part tant que tu ne le soumets pas.

Un plantage est écrit dans `%LOCALAPPDATA%\DrDXT\crash.log`, sur ta machine seulement. Un test échoue si une adresse
contactée par le code manque dans cette liste.

## Compiler

Windows 10 ou 11 x64 et le SDK **.NET 10**. Rien d'autre pour l'application et les tests.

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

**6 796 tests, 0 échec**, mesuré le 10/10/2026 sur 17 projets, hors tests marqués `Machine` (ils lisent le vrai
matériel). Ce chiffre est vérifié par `tools/gates/compte_des_tests.py` contre la dernière exécution. La commande qui
le redonne :

```bash
dotnet test DrDXT.sln -c Release --filter "Category!=Machine" --nologo -v q 2>&1 | grep "Réussi" \
  | sed 's/.*total[^0-9]*\([0-9][0-9]*\).*/\1/' | awk '{s+=$1; n++} END {print n, "projets |", s, "tests"}'
```

L'installeur se construit avec `installer/build-installer.ps1`. Il demande **Inno Setup 6** et les outils C++ de
Visual Studio, pour compiler en natif le lanceur qui reçoit l'élévation.

## Licence

DrDXT est un logiciel libre sous **GPL-3.0**. Le texte complet est dans `LICENSE` et livré avec chaque copie. Tu peux
l'utiliser, le copier, le modifier et le redistribuer, même contre de l'argent, à condition de publier le code source
de ta version sous la même licence. Une version fermée, ou payante sans source, n'est pas permise. Le nom « DrDXT »
désigne ce projet : une version modifiée se présente sous son propre nom.

Livrés à côté : PresentMon (Intel, licence MIT, `PresentMon-NOTICE-MIT.txt`) et QRCoder (licence MIT,
`QRCoder-LICENSE-MIT.txt`). NVAPI (NVIDIA) et ADLX (AMD) ne sont pas livrés : DrDXT appelle les bibliothèques que ton
pilote a installées, d'après leurs en-têtes publics.

## Les cinq invariants

Cinq règles tiennent le code, et chacune vient d'un échec réel :

- **Prouvé, jamais affirmé.** Un gain ne s'affiche qu'au-dessus du bruit de la mesure. « Aucun gain mesurable » est
  une réponse valide, et souvent la bonne.
- **Chirurgical.** Aucun service désactivé, aucun nettoyage de Windows, aucun script greffé. Ce qui exige le BIOS reste
  un guide.
- **Réversible.** Valeur d'origine lue avant toute écriture, retour testé, point de restauration.
- **Honnête sur l'indisponibilité.** Un réglage qui ne s'applique pas dit lequel et pourquoi.
- **Liste noire permanente.** Les mythes de l'optimisation (Nagle, `NetworkThrottlingIndex`, polling 8 000 Hz,
  priorité temps réel, C-states coupés en bloc) sont refusés par un test qui casse si l'un d'eux entre au catalogue.

## Carte du code

| Projet | Rôle |
|---|---|
| `DrDxt.Core` | contrats, résultats, codes d'erreur, opérations privilégiées |
| `DrDxt.Detection` | matériel, état des réglages, réseau |
| `DrDxt.Measurement` | PresentMon, repli DXGI, réseau, test mémoire |
| `DrDxt.Application` | catalogue des réglages, moteur d'application, retour arrière, bornes GPU |
| `DrDxt.ElevatedHelper` | le programme élevé qui écrit, derrière sa liste blanche |
| `DrDxt.Lanceur` | l'exécutable natif qui reçoit l'élévation et relance les autres avec un environnement propre |
| `DrDxt.Reconciler*` | manifeste signé, réapplication au démarrage |
| `DrDxt.Bios` | lecture du BIOS et guide, jamais d'écriture |
| `DrDxt.GpuNative` | NVAPI, ADLX, NVML |
| `DrDxt.App` | l'interface (WPF) |
| `tools/` | bancs et sondes hors solution, dont le zip de validation (`amd-harness`) |

Par où commencer : [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md), [docs/SECURITE.md](docs/SECURITE.md), puis
[docs/INSTRUMENTS.md](docs/INSTRUMENTS.md) et [docs/OU-VIT-LE-GAIN.md](docs/OU-VIT-LE-GAIN.md) pour ce que chaque
mesure peut prouver. Les conventions de travail sont dans [CONTRIBUTING.md](CONTRIBUTING.md).
