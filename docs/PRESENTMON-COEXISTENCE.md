# PresentMon : le geste utilisateur a disparu

**Mesuré le 07/09/2026, machine de référence (i7-14700F / RTX 4060), par le SCM.**
Instrument : `tools/pmoncoexist`. Toutes les manipulations ont été annulées ; la machine a été
remise dans son état initial (service Intel `Running`, service DrDXT `Stopped`).

## Le problème, tel qu'il se présentait

Deux services PresentMon existent sur la machine :

| service | binaire | version | démarrage |
|---|---|---|---|
| `PresentMonSharedService` | `C:\Program Files\Intel\PresentMonSharedService\` | 2.5.1 | **automatique** |
| `PresentMonService` | vendoré par DrDXT | 2.4.1 | manuel |

Symptôme constaté depuis le 02/09 : `PM_STATUS_PIPE_ERROR` (12) **3 fois sur 5**, et un produit qui
demandait à l'utilisateur d'arrêter le service rival — un geste manuel à refaire à chaque
démarrage, puisque le service Intel démarre en automatique.

L'hypothèse de travail était un **conflit de noms** : les deux services publient
`\\.\pipe\sharedpresentmonsvcnamedpipe`, le premier arrivé gagne. Le binaire vendoré documente
d'ailleurs l'option dans ses propres chaînes d'aide :

```
--control-pipe   Name of the named pipe to use for the client-service control channel
```

La correction semblait donc évidente : donner à DrDXT un tube privé.

## Ce que la machine a répondu

### Le contrôle qui rend tout le reste lisible

Lancé **en console** (`--debug`), le service ne crée **jamais** de tube de contrôle — même sans
aucune option, même service Intel arrêté. Il ne crée que son tube de log (`shared-pm2-svc-log`).
**Aucun essai en console ne prouve quoi que ce soit sur cette question.** Seule la mesure par le
SCM compte. Trois heures ont été perdues sur des essais console avant de poser ce contrôle.

### Mesure 1 — l'option est acceptée et sans effet

Service DrDXT 2.4.1, démarré **par le SCM**, service Intel **arrêté**, ligne de commande vérifiée
dans `Win32_Process.CommandLine` :

```
"…\third_party\presentmon\PresentMonService.exe" --control-pipe \\.\pipe\drdxt-presentmon-control
    --intro-nsm drdxt-presentmon-intro --etw-session-name drdxt-presentmon-etw
```

Tubes publiés : `sharedpresentmonsvcnamedpipe-in` et `-out`. **Le nom par défaut.**
Aucun tube `drdxt-*`. Le service est `Running`, vivant, et n'a émis aucune erreur.

Essayé aussi : `--control-pipe` seul ; `--control-pipe` + `--intro-nsm` ; le nom sans le préfixe
`\\.\pipe\`. **Aucune combinaison ne change le nom publié.**

L'option existe pour le **client**, pas pour le serveur.

### Mesure 2 — monter la version du service ne changerait rien

Même essai avec le binaire **Intel 2.5.1**, enregistré comme service jetable
(`drdxt-pm251-essai`, supprimé depuis) avec `--control-pipe \\.\pipe\drdxt-presentmon-control` :
publie `sharedpresentmonsvcnamedpipe-in/-out`. Le nom par défaut, là encore.

À noter au passage : **2.5.1 a une autre ligne de commande** — il rejette `--intro-nsm`
(« The following arguments were not expected »).

### Mesure 3 — ce n'est pas seulement un conflit de noms

| client | service actif | tube demandé | résultat |
|---|---|---|---|
| API 2.4.1 | Intel 2.5.1 | `\\.\pipe\sharedpresentmonsvcnamedpipe` | `PM_STATUS=12` |
| API 2.4.1 | Intel 2.5.1 | `pmOpenSession` (défaut implicite) | `PM_STATUS=12` |
| API 2.4.1 | **DrDXT 2.4.1** | `\\.\pipe\sharedpresentmonsvcnamedpipe` | **SUCCÈS** |
| API 2.4.1 | DrDXT 2.4.1 | `pmOpenSession` | **SUCCÈS** |

Le client 2.4.1 **atteint** le service Intel et se fait refuser : l'incompatibilité est de
**version**, pas d'adressage. Arrêter le rival ne « libère » donc pas un nom — ça remplace un
service que le client ne sait pas lire par un service qu'il sait lire.

## Conclusion — CORRIGÉE LE MÊME JOUR, ET ELLE ÉTAIT TROP GÉNÉRALE

Ce document a d'abord conclu « la cohabitation des deux services est hors d'atteinte ». **C'est
faux, et la machine l'a démenti deux heures plus tard.**

Ce que les mesures 1 à 3 autorisent, et rien de plus :
**la paire de versions utilisée par DrDXT (client 2.4.1) ne cohabite pas avec le service Intel
2.5.1 installé sur cette machine.** Elles ne disaient rien d'une paire ASSORTIE — et c'était
justement la question qui décide si le geste utilisateur peut disparaître.

### Mesure 4 — une paire assortie fonctionne, service Intel ACTIF

Aucun téléchargement n'a été nécessaire : la machine porte déjà la paire 2.5.1 complète
(`PresentMonAPI2.dll` **et** `PresentMonService.exe`, sous
`C:\Program Files\Intel\PresentMonSharedService\`). Instrument : `tools/pmonpair`, qui charge une
DLL cliente désignée par argument — chaque version dans son propre processus, sans quoi la première
DLL chargée servirait aux deux essais sans que rien ne le signale.

| client | service Intel | `pmOpenSession` | `pmStartTrackingProcess` |
|---|---|---|---|
| **2.5.1** | Running | **SUCCÈS** | **SUCCÈS** (explorer.exe) |
| 2.4.1 (vendoré) | Running | PIPE_ERROR (12) | — |

⇒ **Un client assorti au service déjà présent ouvre une session et suit un processus, sans arrêter
quoi que ce soit.** Le problème n'est ni le nom du tube, ni la cohabitation : c'est **la version du
client vendoré**.

### Et la montée coûte presque rien

Le coût annoncé plus bas comme « à ne pas sous-estimer » a été mesuré, headers en main
(`third_party/presentmon/PresentMonAPI.h` contre celui du SDK 2.5.1 installé) :

- **les 89 valeurs de `PM_METRIC` sont identiques ET dans le même ordre** ; 2.5.1 en ajoute trois à
  la fin — dont **`PM_METRIC_SESSION_START_QPC`**, précisément la base temporelle commune dont la
  timeline root-cause a besoin ;
- **`PM_QUERY_ELEMENT` est inchangée**, champ pour champ.

L'ABI que DrDXT utilise est donc **compatible**. Le code client n'a rien à changer : seul le binaire
vendoré doit l'être.

Ce que le produit a gagné quand même, et qui est réel :

- le client ouvre son tube **explicitement** (`pmOpenSessionWithPipe`) au lieu de dépendre d'un
  défaut implicite — une seule constante décide à quel service DrDXT parle ;
- le cycle de vie du service distingue enfin **INSTALL / START / REPAIR**, donc une machine où
  `PresentMonService` pointe vers un binaire inconnu se répare au lieu de mesurer à l'aveugle ;
- le produit **émet enfin `InstallPresentMonServiceOp`** (`SelfHealingBenchmarkRunner`) : l'op
  était écrite, allowlistée, testée et envoyée par personne ;
- le scan ne dit plus « lance une partie » quand la cause est le service ;
- `sc.exe` : un `binPath` avec arguments et guillemets non échappés rend **1639** et ne crée
  rien — mesuré, corrigé, testé.

## La voie, et ce qui la bloque encore

**Vendorer la paire 2.5.x upstream** (client + service de la même release) et consommer le service
Intel quand il est déjà présent. Le geste utilisateur disparaît alors complètement.

Ce qui reste à faire, et c'est peu :

- déposer la release upstream dans `third_party/presentmon` — **gate du mainteneur** : je ne recopie pas
  le binaire installé par Intel dans le dépôt, sa provenance n'est pas la release MIT que nous
  vendorons ;
- `PresentMonInteropTests` fige les valeurs de `PM_METRIC` en dur : **vérifié compatible**, les 89
  premières sont identiques et dans le même ordre ;
- `PM_QUERY_ELEMENT` : **vérifiée identique** ;
- garder l'installation du service comme **repli**, pour la machine sans Intel PresentMon ;
- ne pas forker PresentMon.

## Ce qu'il ne faut pas refaire

- Remettre `--control-pipe` dans l'installeur ou dans le helper. Deux tests le refusent
  (`PresentMonNamingContractTests`, `PresentMonNamingTests`) : il a été essayé, mesuré, il n'a
  aucun effet, et le remettre réintroduirait une **explication fausse** du conflit.
- Conclure quoi que ce soit d'un lancement en console. Voir le contrôle en tête de page.


---

## FERMÉ LE 07/09/2026 — la paire 2.5.1 est vendorée, et la coexistence est prouvée

La release officielle a été téléchargée depuis
`github.com/GameTechDev/PresentMon/releases/tag/v2.5.1` (`PresentMon-v2.5.1.msi`, 157 696 000
octets, SHA256 `4DDE95A7…`), extraite par `msiexec /a` — une installation **administrative**, qui
extrait sans rien installer sur la machine. `NOTICE-MIT.txt` et `THIRD_PARTY.txt` viennent du même
artefact. Empreintes et provenance dans `third_party/presentmon/VERSION.txt`.

**Aucun binaire n'a été recopié depuis l'installation Intel de la machine** : sa provenance
n'aurait pas été la release que nous vendorons.

### Ce que la machine répond maintenant, service Intel ACTIF

```
  PresentMonSharedService    Running          <- le service d'Intel, jamais arrêté
  PresentMonService          Stopped          <- le nôtre n'a même pas besoin de tourner

  tube du produit   \.\pipe\sharedpresentmonsvcnamedpipe   -> SUCCES
  IsServiceAvailable : IsOk=True valeur=True
  detail             : (aucun — la session s'est ouverte)
```

Et une **capture réelle** dans la foulée, toujours avec Intel actif :

```
  RESULT OK: frames=49 avgFps=8,0 ... (dwm.exe)
```

⇒ **Le geste « arrête le service rival » disparaît du parcours utilisateur.** DrDXT consomme le
service déjà présent. Sur une machine sans Intel PresentMon, l'installation du service vendoré
reste le repli, et `SelfHealingBenchmarkRunner` s'en charge tout seul.

### Ce que la montée a coûté

Rien côté code. Vérifié **avant** de bouger quoi que ce soit : les 89 valeurs de `PM_METRIC` sont
identiques et dans le même ordre, `PM_QUERY_ELEMENT` est inchangée. La suite complète est passée à
3 109 verts sans qu'une seule ligne de client soit touchée — `PresentMonInteropTests`, qui fige ces
valeurs en dur, comprise.

Seule différence observée : 2.5.1 rejette `--intro-nsm`. Sans conséquence, le produit ne passant
aucun argument au service.
