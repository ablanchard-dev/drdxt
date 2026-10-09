# DrDXT

Optimisation PC gaming pour Windows. Le produit détecte le matériel, lit l'état des réglages,
applique des leviers réversibles, **mesure** (PresentMon, ou les événements DXGI de Windows quand
PresentMon ne voit rien), annule tout seul un levier immédiat qui régresse en session a-b-b-a, et
sait réasserter au boot ce qui doit persister. Ce dernier point passe par le service
`DrDxtReconciler`, que l'installeur propose (case « reconciler », décochée par défaut) : sans lui,
rien n'est réappliqué au démarrage.

Ce que la mesure ne sait pas faire, et le produit le dit : en partie réelle, le bruit est de 5 à
10 % (`docs/OU-VIT-LE-GAIN.md`), plus que le gain attendu de la plupart des leviers. Un levier
qui ne dépasse pas ce bruit est dit « indistinguable », jamais « gagnant ». Les leviers qui
prennent effet au redémarrage sont appliqués sans mesure immédiate : seule l'analyse suivante en
jeu peut en juger.

Le mot qui compte : **mesure**. Le marché de l'optimisation PC est un marché de placebos. Tout ce
qui distingue ce produit tient dans une discipline : il ne dit jamais un chiffre qu'il n'a pas
mesuré, et il n'applique jamais un levier qu'il ne peut pas défaire.

C# / .NET 10 (`net10.0-windows`). **6 666 tests, 0 échec**, mesuré le 09/10/2026 sur
17 projets, hors tests marqués `Machine`. Un chiffre écrit à la main vieillit (celui-ci s'est
trompé sept fois) : `tools/gates/compte_des_tests.py` compare cette ligne aux journaux `.trx` de la
dernière exécution, et ne dit rien du tout s'il n'y en a pas. La commande qui redonne le chiffre :

```bash
dotnet test DrDXT.sln -c Release --filter "Category!=Machine" --nologo -v q 2>&1 | grep "Réussi" \
  | sed 's/.*total[^0-9]*\([0-9][0-9]*\).*/\1/' | awk '{s+=$1; n++} END {print n, "projets |", s, "tests"}'
```

Le `sed` permissif n'est pas de la coquetterie : `dotnet test` sépare ses libellés de leurs
deux-points par une **espace insécable**, et un motif qui attend une espace ordinaire rend
zéro sans rien dire. Et les deux options comptent : sans `-c Release`, 5 tests de plus (compilés en
Debug seulement) ; sans le filtre, les tests `Machine` en plus.

## Compiler

Prérequis : Windows 10 ou 11 x64, le SDK **.NET 10**. Rien d'autre pour l'application et les tests.

```
dotnet build
dotnet test
dotnet publish src/DrDxt.App -c Release -r win-x64 --self-contained true -o publish
```

L'installeur se construit avec `installer/build-installer.ps1`, qui demande en plus
**Inno Setup 6** (`ISCC.exe`). Sans certificat de signature de code, Windows SmartScreen avertira
à la première ouverture : l'installeur n'est pas signé.

## Ce que DrDXT écrit sur ta machine

Les réglages passent par le helper élevé, qui refuse tout ce qui n'est pas dans sa liste
(`src/DrDxt.ElevatedHelper/OpAllowlist.cs`). Trois écritures n'y passent pas, et c'est voulu : le
service de démarrage (SYSTEM) applique la même liste dans son propre processus ; l'appli baisse la
priorité des programmes voisins pendant une partie (rendue à la fin de la partie) ; la réparation de
Windows ouvre une console administrateur (`DISM`, `sfc`) que tu valides toi-même. Chaque réglage capture d'abord la valeur d'origine,
et se défait. La liste complète :

- **Registre, valeurs exactes** (jamais une branche entière) : `HwSchMode` (planification GPU
  accélérée par matériel), `PagingFiles` (fichier d'échange désactivé réparé), `AllowAutoGameMode`
  et `AutoGameModeEnabled` (mode Jeu), `HistoricalCaptureEnabled` (enregistrement en arrière-plan
  de Game DVR, la capture manuelle reste), `DirectXUserGlobalSettings` (VRR et modèle flip, Auto HDR
  conservé), `MouseSpeed`, `MouseThreshold1` et `MouseThreshold2` (accélération du pointeur coupée,
  les trois rendues à l'identique). Deux autres ne sont plus jamais posées, seulement rendues si une ancienne version les
  a écrites : `LowLatencyMode` (pilote NVIDIA) et `CppcEnable` (AMD X3D). Aucune source ne montre
  que le pilote ou Windows les lise.
- **Pilote NVIDIA, quatre réglages**, au profil du jeu seulement, jamais pour tout le PC : file de
  rendu, synchronisation verticale, limiteur d'images, mode d'alimentation.
- **Plan d'alimentation** : seulement un plan Windows connu.
- **Un point de restauration** avant d'appliquer, et la restauration système activée sur le seul
  disque système si elle ne l'était pas.
- **Deux services** : PresentMon (mesure des images, installé par l'installeur) et le service AMD
  3D V-Cache (démarrer, arrêter, interroger).
- **Redémarrer vers le BIOS**, sur ta demande, avec un délai de 5 minutes au plus.
- **Le manifeste signé des réglages appliqués**, avec sa clé de signature propre à ta machine,
  dans `%ProgramData%\DrDxt` : le manifeste se lit par tous et ne se modifie que par les Administrateurs et SYSTEM ;
  la clé de signature n'est lisible que par eux. C'est lui que la
  désinstallation relit pour rendre ce que DrDXT a posé, seulement si la valeur est encore celle
  que DrDXT avait mise : un réglage changé à la main depuis est laissé tel quel. Un levier posé
  par une version d'avant le 06/10/2026 dans ta session Windows (VRR, mode Jeu, Game DVR) n'y
  est pas : la désinstallation ne le rend pas, « Annuler » dans la carte « Ce que DrDXT a posé » le rend. Le fichier d'échange remis en route par DrDXT
  n'est pas rendu non plus (le repasser à « aucun » referait la panne). Ce que la désinstallation
  laisse, lu sur une vraie désinstallation le 06/10/2026 : dans `%ProgramData%\DrDxt`, son rapport
  (`desinstallation-<date>.txt`), l'ancien manifeste renommé, les clés de la machine et l'état du
  service ; dans `%LOCALAPPDATA%\DrDXT`, ton historique (parties, scans). Aucun service, aucune
  tâche, aucune clé de registre ni aucun raccourci ne restent.
- **Deux politiques QoS** (plafond d'envoi pendant le jeu, sur ta demande) : posées au lancement du
  jeu, retirées à sa fermeture, dans le magasin actif de Windows, donc effacées par un redémarrage.
  Si DrDXT s'arrête sans les retirer, le helper les retire seul au bout de 30 secondes, et la
  désinstallation les retire aussi.
- **Démarrage de Windows (BCD), cinq éléments exacts**, sur ta demande : `bootstatuspolicy`,
  `useplatformclock`, `useplatformtick`, `disabledynamictick`, `tscsyncpolicy`, avec une liste
  fermée de valeurs. Le geste proposé retire un réglage posé par un script ; « Annuler » repose
  la valeur lue avant. La désinstallation, elle, ne la repose pas : le démarrage reste à la
  décision par défaut de Windows.

Le helper écrit aussi la carte graphique, toujours dans des bornes. La **limite de puissance** est
essayée en jeu par « Optimiser » et gardée seulement avec un gain mesuré, sinon rendue. En **mode
Expert** (page Graphique), sur ton accord coché et jamais pendant une partie : le **décalage
d'horloge**, et sur un PC fixe à une seule carte NVIDIA une **vitesse fixe du ventilateur** (de 40 à
100 %, jamais sous ce que la carte vise au moment de la pose ; elle ne suit plus la température tant
que tu ne la rends pas). Un réglage Expert ne dure que la session : si DrDXT s'arrête, le helper le
rend seul au bout de 2 minutes, et le lancement suivant le rend s'il restait. Une courbe de
ventilation à plusieurs points n'est pas écrite : le helper la refuse.

Jamais écrit, quoi qu'on demande : Windows Defender, Windows Update, BITS, `bam`, les stratégies de
restriction logicielle, `SECURITY`, `SAM`, et le firmware (le BIOS est **lu**, jamais écrit).

## Ce qui quitte la machine

Rien qui te désigne : pas de télémétrie, pas de compte, pas de licence à activer. Voici tout ce que DrDXT envoie sur le
réseau, et pourquoi. Ce que DrDXT lit et garde sur ta machine est dans [CONFIDENTIALITE.md](CONFIDENTIALITE.md).

- **La dernière version des pilotes graphiques**, à chaque analyse, pour te dire si ton pilote a du retard : une
  lecture de la page publique d'AMD (`www.amd.com`) et une requête fixe au service public de NVIDIA
  (`gfwsl.geforce.com`, toujours la même, série RTX 40, Windows 11). Elles ne portent rien qui te désigne ni le modèle
  de ta carte ; le serveur voit seulement qu'un PC Windows demande la dernière version (et NVIDIA n'est interrogé que
  sur une carte GeForce).
- **Des pings** (ICMP, sans contenu) vers ta box et vers `1.1.1.1`, pour mesurer la latence et savoir si elle vient de
  chez toi ou d'au-delà ; pendant une partie, aussi vers le serveur du jeu, pour suivre la route d'une partie à l'autre.
- **La vérification de mise à jour**, au démarrage : deux petits fichiers lus sur `https://ablanchard-dev.github.io/drdxt`
  (le site GitHub Pages du projet ; GitHub voit donc ton adresse IP, comme pour toute page web). Rien de ton PC n'est
  envoyé. Pour la couper : variable d'environnement `DRDXT_UPDATE_URL` posée à `off` (« aucun serveur configuré » en bas de
  l'écran).
- **La page support de ta carte mère MSI, seulement si tu cliques « Ouvrir la page de ta carte »** dans le guide du BIOS : ton
  navigateur ouvre `www.msi.com/Motherboard/<modèle>/support`. DrDXT ne télécharge rien lui-même.
- **Ton rapport de vérification, seulement si tu cliques « Envoyer mon rapport »** : DrDXT n'envoie rien lui-même, il
  ouvre ton navigateur sur un ticket GitHub déjà rempli (le rapport, sans rien qui te désigne). Rien ne part tant que tu
  ne le soumets pas.

Un plantage est écrit dans `%LOCALAPPDATA%\DrDXT\crash.log`, sur ta machine seulement : rien ne l'envoie. Les
adresses contactées vivent dans des constantes nommées, et chacune doit être nommée ici :
`CeQuiQuitteLaMachineEstDitTests` échoue sinon. Sa limite : il lit les adresses des fichiers qui font eux-mêmes
l'appel réseau.

## Licence

DrDXT est un logiciel libre sous **GPL-3.0** : le texte complet est dans `LICENSE`, et il est livré
avec chaque copie du programme. PresentMon (Intel) est sous licence MIT, sa notice est livrée à
côté (`PresentMon-NOTICE-MIT.txt`, depuis `third_party/presentmon/NOTICE-MIT.txt` dans ce dépôt). QRCoder,
qui dessine le QR du guide BIOS, est sous licence MIT : sa notice est livrée à côté (`QRCoder-LICENSE-MIT.txt`,
depuis `third_party/qrcoder/LICENSE-MIT.txt`).

NVAPI (NVIDIA) et ADLX (AMD) ne sont pas livrés avec DrDXT : il appelle les bibliothèques que le
pilote de ta carte a installées (`nvapi64.dll`, `amdadlx64.dll`). Les numéros d'interface viennent des
en-têtes publics : NVAPI est publié par NVIDIA sous licence MIT (github.com/NVIDIA/nvapi), ADLX par AMD
sous son propre contrat de licence de SDK (github.com/GPUOpen-LibrariesAndSDKs/ADLX).

## Par où commencer

1. **`docs/NON-NUISANCE.md`** et **`docs/SECURITE.md`** : ce que DrDXT peut écrire sur la machine,
   ce qui est attaquable, et le test qui tient chaque garde.
2. **`docs/INSTRUMENTS.md`** et **`docs/OU-VIT-LE-GAIN.md`** : ce que chaque mesure peut prouver, et
   où un gain peut exister, ou pas.

## Les cinq invariants

Ce ne sont pas des préférences de style : la moitié du code existe pour les tenir, et chacun vient
d'un échec réel.

- **Prouvé, jamais affirmé.** Un gain ne s'affiche qu'au-dessus de la marge d'erreur, et la marge
  est la variance *entre runs* (≥3), pas un IC par frame : les frametimes sont autocorrélés, et un IC
  par frame sort une marge trop serrée et invente des gains. Sous 3 runs, la marge vaut `+∞`.
  « Aucun gain mesurable » est une réponse valide, et souvent la bonne.
- **Chirurgical.** Aucun service désactivé, aucun debloat, aucun script greffé. Un levier qui exige
  le BIOS reste de la *guidance* : on explique, on n'écrit jamais dans le firmware. Et si la
  recherche conclut qu'un levier n'est pas chirurgical, il ne part pas en auto-apply, même s'il
  est facile à coder.
- **Réversible.** Valeur d'origine capturée par lecture réelle avant toute écriture, sérialisée en
  op inverse, plus un point de restauration vérifié. Le revert est testé en CI.
- **Honnête sur l'indisponibilité.** Rien n'est sauté en silence : si un levier ne s'applique pas,
  le produit dit lequel et pourquoi, avec un code distinct.
- **Liste noire permanente.** Les mythes (Nagle/`TcpAckFrequency`, `NetworkThrottlingIndex`,
  debloat de services, polling 8000 Hz, timer global, priorité Realtime, C-states off en bloc…)
  sont rendus *inshippables* par un test qui casse si l'un d'eux entre au catalogue.

## Carte du code

| Projet | Rôle |
|---|---|
| `DrDxt.Core` | contrats, `Result`/`Error`, codes d'erreur, ops privilégiées + codec |
| `DrDxt.Detection` | matériel, état des réglages, réseau (et un score /100 interne, jamais affiché) |
| `DrDxt.Measurement` | PresentMon (capture, 1 % lows, A/B, marge entre runs), repli DXGI par ETW, réseau (bufferbloat), test mémoire en session |
| `DrDxt.Application` | catalogue de tweaks, moteur transactionnel LIFO, sûreté, bornes GPU |
| `DrDxt.ElevatedHelper` | le code qui écrit les réglages : exécuteur d'ops typées + allowlist ; il tourne élevé, sauf pour les valeurs de ta session (HKCU), écrites par l'App avec ton jeton, après la même allowlist |
| `DrDxt.Reconciler*` | état désiré signé, détection de dérive, réassertion au boot |
| `DrDxt.Bios` | BIOS Doctor : détecte → diffe → guide, jamais d'écriture |
| `server/` | canal de mise à jour : manifeste signé Ed25519 (graine privée en variable d'env) |
| `DrDxt.GpuNative` | NVAPI / ADLX / NVML en P/Invoke |
| `tools/*` | smokes live hors solution (`netprobe`, `r2probe`, `m2smoke`, `amd-harness`…) |

**Modèle de sécurité** : tout réglage passe par une op typée sérialisée vers le helper élevé,
validée par une allowlist *default-DENY, feuille-exacte*. Pas de chaîne libre, pas de chemin de
registre paramétrable. Ajouter une feuille est un changement sécu-sensible : revue adverse, pas en
passant. Le service réconciliateur tourne en SYSTEM et est *structurellement incapable* d'écrire le
GPU, et c'est voulu.

## Conventions

- **Chemins explicites au commit, jamais `git add -A`** : des fichiers non suivis ont déjà été
  détruits comme ça.
- **TDD**, et le test doit *prouver* la garantie, pas la nommer : casse le code volontairement pour
  vérifier qu'il échoue. Un test vert sur une garantie vide est pire que pas de test.
- **Exécuter, pas balayer** : un défaut trouvé à la lecture est une hypothèse, un défaut trouvé en
  lançant est un fait. `tools/` existe pour ça.
- Messages de commit en français, factuels, ils expliquent le *pourquoi* et le défaut évité.
