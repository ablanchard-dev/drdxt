# Ce qui fait bouger la latence, la fluidité et les FPS, et ce que DrDXT en fait

Ce document répond à une seule question : **sur un PC Windows, qu'est-ce qui fait gagner de la latence
(souris ⇒ écran), de la fluidité (images stables), de l'input lag et des FPS, et DrDXT le fait-il tout seul ?**

Trois états, et rien d'autre :

- **écrit** : DrDXT le règle lui-même, sous garde (origine lue, retour testé, annulé sans gain mesuré, rendu seul
  après un incident) ;
- **lu et dit** : DrDXT le lit sur ta machine (page Docteur), te dit si c'est bon, et te donne le geste si ce n'est pas
  le cas ;
- **à la main** : personne ne peut le faire à ta place (BIOS, matériel, réglages dans le jeu, box Internet). Les
  étapes sont plus bas.

Ce guide vaut aussi pour un PC de bureau sans jeu : la latence de la souris et des fenêtres, la fluidité du bureau et
la mémoire pleine (5.17) se lisent et se corrigent de la même façon.

Un gain n'existe que là où un défaut existe : sur un PC déjà bien réglé, la plupart de ces lignes ne changent
rien, et DrDXT te le dit plutôt que d'inventer un chiffre (voir `docs/OU-VIT-LE-GAIN.md`). Chaque source a été
relue le 09/10/2026.

## 1. Latence et input lag (souris ⇒ écran)

| Ce qui bouge | Mécanisme | État | Source |
|---|---|---|---|
| File de rendu du pilote (images pré-rendues) | Le processeur prend de l'avance sur la carte : chaque image d'avance est une image de retard à l'écran. « Off » = le jeu décide (1 à 3 images), « On » = 1, « Ultra » = 0. | **écrit** (`latency.render_queue_cap`, « On », au profil du jeu ; jamais par-dessus Reflex) | NVIDIA, [guide de la latence système](https://www.nvidia.com/en-us/geforce/guides/gfecnt/202010/system-latency-optimization-guide/) |
| Reflex dans le jeu | Le jeu lui-même cale le processeur « juste à temps » : la file disparaît et la souris est lue au plus près de l'image. Plus fort que le réglage du pilote. | **à la main** (option du jeu ; DrDXT lit le fichier de réglages de Call of Duty et te dit si Reflex est activé) | NVIDIA, [guide de la latence système](https://www.nvidia.com/en-us/geforce/guides/gfecnt/202010/system-latency-optimization-guide/) |
| Plafond d'images sous la fréquence de l'écran, avec G-SYNC/FreeSync | Au-dessus de la fréquence, l'écran retombe en V-Sync et la latence grimpe ; 3 images par seconde (FPS) sous le maximum, elle redevient proche du V-Sync off. | **écrit** (`latency.vrr_vsync_cap` : VRR + V-Sync pilote + plafond au profil du jeu) | Blur Busters, [G-SYNC 101](https://forums.blurbusters.com/viewtopic.php?t=3441) |
| Fréquence de sondage de la souris | 125 Hz = jusqu'à 8 ms entre deux lectures de la souris ; 1 000 Hz = 1 ms. | **lu et dit** (DrDXT lit la fréquence annoncée ; le réglage se fait dans le logiciel de la souris) | Blur Busters, [guide souris](https://www.blurbusters.com/faq/mouse-guide/) |
| Suspension sélective USB des périphériques d'entrée | Un périphérique suspendu met du temps à se réveiller. | **lu et dit** : DrDXT lit si la suspension est active sur tes périphériques d'entrée et le dit. | Microsoft Learn, [USB selective suspend](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-selective-suspend) |
| Mode Jeu de Windows | Pendant la partie, Windows Update n'installe pas de pilotes et ne demande pas de redémarrage. | **écrit** (`latency.gameModeOn`) | Microsoft, paramètres du Mode Jeu |
| Enregistrement Game DVR en arrière-plan | Capture continue : du processeur, de la carte et du disque pris en permanence. | **écrit** (`latency.gameDvrBackgroundOff`) | Microsoft, Xbox Game Bar |
| Enregistrement continu NVIDIA (Instant Replay) | Même coût, côté pilote : environ 5 % d'images selon la FAQ ShadowPlay de NVIDIA, non mesuré par DrDXT. | **lu et dit** (geste : Alt+Z, Instant Replay, Désactiver) | NVIDIA, FAQ ShadowPlay |
| Planification GPU à accélération matérielle (HAGS) | La carte gère sa propre file de travail au lieu du processeur ; l'effet sur la latence n'est pas mesuré. | **écrit** (`latency.hagsOn`, au redémarrage) | Microsoft, [redesign des réglages graphiques](https://devblogs.microsoft.com/directx/navigating-the-redesigned-graphics-settings-page/) |
| Optimisations pour les jeux en fenêtre (modèle flip) | Les jeux en fenêtre ou en fenêtre sans bordure passent du vieux modèle « blt » au modèle « flip » : moins de latence de présentation, VRR possible. | **écrit** (`latency.vrrFlipModelOn`) | Microsoft, [Optimizations for windowed games](https://support.microsoft.com/en-us/windows/optimizations-for-windowed-games-in-windows-11-3f006843-2c7e-4ed0-9a5e-f9389e535952) |
| Plein écran exclusif contre optimisations plein écran | Windows fait tourner le plein écran « exclusif » en fenêtre sans bordure optimisée ; Microsoft mesure des performances égales. | **rien à faire** (ne pas désactiver les optimisations plein écran sans mesure) | Microsoft, [Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/) |
| Accélération du pointeur (« Améliorer la précision du pointeur ») | La souris n'est plus proportionnelle au mouvement : la visée devient incohérente. Pas de latence, mais de la justesse. | **écrit** (`latency.mouse_acceleration_off`, réglage de session, rendu à l'identique ; effet à la prochaine ouverture de session) | Microsoft, paramètres de la souris |
| Overlays (Discord, Steam, GeForce) | Chaque overlay dessine par-dessus le jeu : du temps processeur et parfois de la latence de présentation. | **lu et dit** (DrDXT nomme ce qui tourne à côté du jeu) | mesure DrDXT (voisins de la partie) |
| Fréquence d'affichage sous le maximum de l'écran | Un écran 144 Hz laissé à 60 Hz fait plus que doubler le temps avant chaque image (16,7 ms contre 6,9). | **écrit** (`fixit.ecran_frequence`) | mesure DrDXT (EDID contre réglage courant) |
| Écran branché sur la carte mère au lieu de la carte graphique | L'image traverse deux cartes : latence et FPS perdus. | **lu et dit** (geste : rebrancher sur la carte graphique) | mesure DrDXT |

## 2. FPS et fluidité (images stables)

| Ce qui bouge | Mécanisme | État | Source |
|---|---|---|---|
| Mémoire sous son profil (XMP / EXPO coupé) | La mémoire tourne à sa vitesse de secours : le processeur attend. | **à la main** (BIOS ; étapes plus bas) ; DrDXT le lit et le dit | fabricants (AMD EXPO, Intel XMP) ; ordre de grandeur, pas de chiffre affichable (`docs/OU-VIT-LE-GAIN.md` G-02) |
| Simple canal (une barrette, ou deux mal placées) | La moitié de la bande passante mémoire. | **lu et dit** (geste : A2 et B2 sur quatre emplacements, manuel de la carte) | `docs/OU-VIT-LE-GAIN.md` G-01 (12 % en moyenne, jusqu'à 34 % dans le pire cas) |
| Limite de puissance du processeur (PL1/PL2) | Un processeur tenu à 65 W tient sa fréquence quelques secondes puis redescend : il devient le goulot. Sur un Intel non-K, 65 W est la valeur d'Intel, pas un défaut de la carte mère. | **à la main** (BIOS ; étapes plus bas) ; DrDXT lit le bridage en jeu et le dit | Intel, fiches techniques (Processor Base Power = PL1, Maximum Turbo Power = PL2) ; mesure DrDXT |
| Plan d'alimentation et boost | Un plan d'économie ou un boost désactivé coupe la fréquence. | **écrit** (`fixit.power_plan` : Équilibré ⇒ Hautes performances, PC de bureau seulement et seulement quand la mesure en jeu montre le processeur comme goulot ; `fixit.mode_boost`) ; sur un Ryzen X3D à deux blocs, voir 5.12 | Microsoft, options d'alimentation |
| Parcage des cœurs | Des cœurs endormis se réveillent avec retard. | **lu et dit** : DrDXT lit la politique de parcage du plan courant ; sur un Ryzen X3D à deux blocs, le mauvais bloc parqué est corrigé (**écrit** `fixit.x3d_ccd`) | Microsoft Learn, [core parking](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/static-configuration-options-for-core-parking) |
| Limite de puissance de la carte graphique | Une carte bridée en puissance baisse sa fréquence en charge. | **écrit** (`gpu.power_limit`, gardé seulement avec un gain mesuré en jeu) | NVIDIA NVAPI ; mesure DrDXT (bridage lu pendant la partie) |
| Fréquence et tension de la carte, courbe de ventilation | Un décalage d'horloge ou une sous-tension : quelques pour cent au mieux sur une carte récente, un plantage au pire. | **mode Expert**, sur ton accord, avec test de stabilité et retour automatique | `docs/OU-VIT-LE-GAIN.md` G-03 (environ 2 %) |
| Resizable BAR / Above 4G | La carte voit toute sa mémoire d'un coup ; l'effet dépend du jeu. | **à la main** (BIOS ; DrDXT lit l'état et guide) | norme PCI Express |
| Jeux sur un disque dur | Chargements et micro-saccades de streaming de textures. | **lu et dit** (geste : déplacer le jeu sur le SSD) | mesure DrDXT |
| Programmes au démarrage, logiciels à côté du jeu | Chacun prend de la mémoire et parfois du processeur pendant la partie. | **lu et dit** (geste : Gestionnaire des tâches > Applications de démarrage) | `docs/OU-VIT-LE-GAIN.md` G-04 (5 à 15 % sur une machine contrainte, souvent rien sur une machine large) |
| Pilote graphique en retard | Corrections de performance et de stabilité manquées. | **lu et dit** (geste : application NVIDIA ou AMD) | NVIDIA / AMD |
| Bridage thermique | Un processeur ou une carte trop chauds baissent leur fréquence. | **lu et dit** (températures et bridage lus en jeu ; la température du processeur vient de HWiNFO s'il tourne, sinon de PawnIO si tu l'as installé et que DrDXT est lancé en administrateur (5.18), sinon elle est dite non lue) ; **mode Expert** pour la courbe de ventilation | mesure DrDXT ; Intel SDM vol. 4 (registres 0x1A2, 0x1B1) |
| Réglages dans le jeu (V-Sync, plafond, plein écran, DLSS) | Le V-Sync du jeu ajoute une image de latence ; un plafond du jeu est le plus bas en latence. | **à la main** ; DrDXT lit les fichiers de réglages de Call of Duty et de certains jeux Unreal et les dit | Blur Busters, [G-SYNC 101](https://forums.blurbusters.com/viewtopic.php?t=3441) |

## 3. Réseau (lag, pics de ping)

| Ce qui bouge | Mécanisme | État | Source |
|---|---|---|---|
| Wi-Fi contre câble | Le Wi-Fi ajoute de la gigue (retransmissions, voisins) que le câble n'a pas. | **lu et dit** (signal, bande, canal lus ; geste : câble) | mesure DrDXT |
| Saturation de l'envoi (bufferbloat) | Un envoi (cloud, streaming) remplit la file de la box : des pics de ping de plusieurs centaines de ms. | **écrit** (plafond d'envoi de ce PC seulement, posé pendant la partie, mesuré) ; **à la main** côté box (SQM / QoS) | [bufferbloat.net](https://www.bufferbloat.net/), OpenWrt SQM |
| Sommeil de la carte réseau (Ethernet vert, économie de lien) | Le lien se rendort entre deux paquets. | **lu et dit** (geste : propriétés de la carte réseau) | mesure DrDXT |

## 4. Ce que DrDXT refuse de faire, et pourquoi

- **Écrire le BIOS** (XMP, PL1/PL2, ReBar, Curve Optimizer) : une écriture ratée empêche la machine de démarrer. DrDXT
  lit, dit, et te donne les étapes ci-dessous.
- **Désactiver le tick dynamique, forcer l'horloge HPET, `useplatformclock`, `tscsyncpolicy`** : modifications
  fréquentes des scripts « d'optimisation », sans effet mesuré ni source, parfois nuisibles. DrDXT les signale si
  elles ont été posées.
- **Couper l'intégrité de la mémoire ou l'hyperviseur** : réglages de sécurité ; DrDXT dit leur coût mesuré, ne les coupe pas.
- **Nettoyer le magasin de composants de Windows (`/ResetBase`)** : irréversible, aucun gain en jeu (de la place disque seulement).
- **Sous-tension du processeur au-delà de ce qui est prouvé** : DrDXT ne monte jamais la tension, ne descend jamais sous
  −125 mV, n'écrit rien sur un processeur qui a SGX (la faille Plundervolt n'attaque que lui), et ne touche jamais au verrou
  « Undervolt Protection » du BIOS : quand il bloque, c'est toi qui choisis de l'ouvrir (5.23). Sur Ryzen, le Curve
  Optimizer est un réglage BIOS (étapes plus bas).

## 5. À la main, étape par étape

La plupart des étapes se défont par le même chemin ; celles qui n'ont pas de retour le disent (**sans retour**). Fais-les
une à la fois, puis joue : DrDXT compare tes parties avant et après et te dit si ça a changé quelque chose. Les noms
d'options changent d'une marque de carte mère à l'autre : le manuel de ta carte fait foi.

### 5.1 Mémoire au profil (XMP sur Intel, EXPO sur AMD)
1. Redémarre et entre dans le BIOS (touche Suppr ou F2 à l'allumage, selon la carte).
2. Cherche « XMP », « EXPO », « DOCP » ou « A-XMP » (onglet OC, Ai Tweaker ou Extreme Tweaker).
3. Choisis le profil 1. Note la fréquence et la tension affichées.
4. Enregistre et redémarre. En DDR5, le premier démarrage peut rester sur écran noir plusieurs minutes (la mémoire
   s'entraîne) : attends jusqu'à 5 minutes sans couper. Si rien ne vient, beaucoup de cartes reviennent seules au
   réglage de secours après trois échecs ; sinon, bouton ou cavalier « Clear CMOS », ou pile retirée une minute (manuel
   de la carte). Le Clear CMOS remet TOUT le BIOS à zéro, pas seulement la mémoire.
5. Relance DrDXT : le constat « mémoire sous son profil » doit disparaître.

### 5.2 Deux barrettes dans les bons emplacements
1. Éteins et débranche le PC. Ouvre le côté.
2. Sur une carte à quatre emplacements, les deux barrettes vont en **A2 et B2** (le deuxième et le quatrième en
   partant du processeur) ; vérifie dans le manuel de ta carte, certaines font exception.
3. Rallume : DrDXT doit dire « double canal ».

### 5.3 Limite de puissance du processeur (Intel)
1. Sache d'abord ce que tu as. Intel publie pour chaque processeur une puissance de base (PL1) et une puissance maximale
   (PL2), et depuis 2024 des « Intel Default Settings » par profil : sur un i7/i9 « K » de 13e ou 14e génération, le profil
   Performance met PL1 = PL2 = 253 W et le profil de base garde PL1 = 125 W ; les deux sont des valeurs d'Intel, une carte
   qui tient PL1 sous la puissance de base bride. Sur un Intel non-K (un i7-14700F par exemple), 65 W de PL1 est la valeur
   d'Intel : monter au-dessus sort de sa spécification, chauffe plus, et sur les 13e et 14e générations demande d'abord le
   microcode à jour (5.14) à cause de l'instabilité connue de ces puces. DrDXT lit la PL1 du registre du processeur (par
   PawnIO, 5.18) ; la carte mère peut tenir plus bas encore par un second registre que DrDXT ne lit pas.
2. BIOS, onglet OC ou Advanced CPU Configuration : « CPU Cooler Tuning » (MSI), « Intel Default Settings » / « Performance
   Preferences » (ASUS), « Turbo Power Limits » (Gigabyte), ou « Long Duration Power Limit » (PL1) et « Short Duration
   Power Limit » (PL2).
3. Prends le profil Intel par défaut adapté à ton refroidisseur (« Intel Default », « Tower Air Cooler ») plutôt qu'une
   valeur à la main. Un PL1 plus haut chauffe plus : surveille la température dans DrDXT.
4. Enregistre, redémarre, joue : le constat « bridage » de DrDXT doit baisser. Retour : le même menu, « Auto » ou le
   profil d'origine.
5. Trop chaud en partie (le constat « chaleur en partie » : 95 °C ou plus) : c'est l'inverse. Une limite ouverte (PL1 = PL2
   = 253 W) laisse passer des pics que le refroidisseur n'évacue pas ; à 100 °C le processeur se freine et les 1 % low
   tombent. DrDXT donne le plafond qui garde le jeu tel quel (la puissance médiane de ta partie, plus un tiers, arrondie :
   117 W en médiane ⇒ 160 W). Optimiser peut poser ce plafond lui-même sur PL1, à chaud (levier « Plafond de puissance
   contre la chaleur », relu sur tes parties suivantes par le docteur, rendu au redémarrage) ; pour le rendre durable et couper aussi les pointes,
   mets PL1 = PL2 à cette valeur au BIOS, même menu (« Long Duration Power Limit » et « Short
   Duration Power Limit » chez MSI). Sur MSI, « CPU Lite Load » un ou deux crans plus bas baisse encore la tension, à
   perf égale ; si un jeu plante, remonte d'un cran. Encore 95 °C ou plus avec ce plafond : c'est le refroidissement
   (montage du ventirad, pâte, second ventilateur, poussière).
6. « 100 °C » n'est pas tout le processeur : le chiffre que lisent DrDXT et la plupart des outils est celui du cœur le
   plus chaud. Sur la machine de référence, à 98 °C lus, le cœur 0 était à 77 °C. Ce qui coûte, c'est le temps passé
   freiné. DrDXT le relève seconde par seconde (par PawnIO) et
   ne conseille rien quand le processeur ne s'est presque pas freiné par la chaleur.
7. Bridé par le courant, pas par la chaleur (le constat le dit : « limite de courant » une bonne part du temps) : au BIOS,
   « CPU Current Limit » (MSI, même menu) en Auto ; une valeur à la main (160 A sur la machine de référence) faisait
   tomber le processeur de 4,9 à 3,9 GHz. Un « CPU Lite Load » très bas peut aussi l'y pousser : remonte d'un ou deux
   crans. Rejoue au moins trois parties avant de juger : une seule ne départage pas un réglage du hasard d'une partie.
8. Le test de refroidissement de DrDXT : deux minutes de charge pleine sur tous les cœurs, l'état du processeur lu chaque
   seconde, arrêt tout seul si le cœur le plus chaud reste à 99 °C dix secondes. Une charge pleine est plus dure qu'un jeu :
   s'il s'arrête, c'est la marge du refroidissement à cette limite de puissance, pas ta partie. Plafonne la puissance
   (étape 5) ou change de refroidisseur ; rejoue-le après, il doit tenir plus longtemps.

### 5.4 Resizable BAR
1. Vérifie d'abord que Windows démarre en UEFI : Windows + R, `msinfo32`, ligne « Mode BIOS » = UEFI. S'il dit
   « Hérité » (Legacy), ne touche à rien : couper le CSM empêcherait le PC de démarrer (conversion `MBR2GPT` d'abord).
2. BIOS, Advanced ou PCI Subsystem Settings : « Above 4G Decoding » sur Enabled, puis « Re-Size BAR Support » sur
   Enabled, CSM désactivé (il l'est déjà si l'étape 1 dit UEFI).
3. Redémarre : DrDXT lit l'état dans le pilote. Retour : les mêmes options sur Disabled.

### 5.5 Curve Optimizer (Ryzen) : moins de chaleur, et la marge pour monter plus haut
1. BIOS, Advanced > AMD Overclocking (AMD affiche un avertissement de garantie : l'accepter engage toi, pas DrDXT) >
   Precision Boost Overdrive > Curve Optimizer.
2. Commence doux : « All cores », « Negative », 10. Enregistre, redémarre. Si le PC ne redémarre plus : 5.1 étape 4.
3. Laisse le PC au repos une heure (les plantages d'un Curve Optimizer trop poussé arrivent souvent en charge légère),
   puis joue plusieurs heures. Un plantage ou un redémarrage = reviens à 5, ou à 0.
4. DrDXT surveille les arrêts brutaux : s'il en voit un après ce changement, il te le dit. Retour : la valeur 0.

### 5.6 Souris : 1 000 Hz et sans accélération
1. Dans le logiciel de ta souris : fréquence de sondage (polling rate) à 1 000 Hz.
2. Windows : Paramètres > Bluetooth et appareils > Souris > Paramètres supplémentaires > Options du pointeur :
   décoche « Améliorer la précision du pointeur » (DrDXT le propose et le fait, si c'est actif chez toi).

### 5.7 Dans le jeu
1. Reflex : « Activé » (ou « Activé + Boost ») quand le jeu le propose.
2. V-Sync du jeu : désactivé ; avec G-SYNC/FreeSync, laisse le V-Sync du pilote et le plafond que DrDXT pose.
3. Plafond d'images du jeu : s'il existe, mets-le 3 FPS sous ta fréquence d'écran (par exemple 141 pour 144 Hz). Le
   plafond du jeu agit avec moins de latence que celui du pilote ; les deux peuvent rester, le plus bas des deux agit.
4. Mode d'affichage : plein écran ou fenêtre sans bordure, les deux se valent sur Windows 11 ; en sans bordure, G-SYNC
   doit être réglé « plein écran et fenêtré » dans le pilote.
5. Fils de rendu (Call of Duty) : Paramètres > Graphismes > Avancé > « Fils de rendu » (RendererWorkerCount dans
   `Documents\Call of Duty\players\s.1.0.codNN.txt0`, −1 = automatique). Les guides conseillent le nombre de cœurs
   performants moins un (un i7-14700 a 8 cœurs performants : 7) ; ce n'est pas mesuré indépendamment, et le jeu réécrit
   ce fichier à chaque sortie : règle-le dans le menu, jeu ouvert. DrDXT lit la valeur, ne l'écrit pas, et juge le
   changement sur tes parties.
6. Éclairage global et reflets (jeux Unreal Engine 5, par exemple JeuB ou Fortnite) : en « épique » ou « cinématique »,
   ces deux réglages font tourner Lumen, l'éclairage le plus lourd pour la carte graphique (guide de performance de
   Lumen d'Epic). Passe-les en « élevé » dans les options graphiques du jeu, puis en « moyen » s'il en faut plus. Ça
   n'aide que si la carte freine : si tes parties disent que c'est le processeur, DrDXT le dit et ne conseille rien.
   DrDXT lit la valeur dans `GameUserSettings.ini`, ne l'écrit pas, et juge le changement sur tes parties.

### 5.8 Réseau
1. Câble Ethernet plutôt que Wi-Fi, chaque fois que c'est possible.
2. Sur la box ou le routeur : active le « SQM », « Smart Queue Management » ou « QoS adaptatif » si elle le propose,
   avec des débits légèrement sous ceux de l'abonnement (environ 90 %). Sinon, DrDXT plafonne l'envoi de la machine
   pendant la partie.
3. Pendant la partie, pas d'envoi lourd (cloud, mise en ligne de vidéos) sur le même réseau.

### 5.9 Programmes au démarrage et à côté du jeu
1. Programmes au démarrage : sous « Comment corriger » du constat, DrDXT coupe chaque programme sur ton clic, comme le
   Gestionnaire des tâches, et « Annuler » le réactive (« SecurityHealth » reste). À la main : Gestionnaire des tâches
   (Ctrl + Maj + Échap) > Applications de démarrage.
2. Overlays : coupe ceux dont tu ne te sers pas (Discord : Paramètres > Overlay du jeu ; GeForce : Alt+Z, Paramètres).
3. Instant Replay de NVIDIA (enregistre en continu, environ 5 % d'images selon NVIDIA) : Alt+Z > Instant Replay >
   Désactiver, si tu ne t'en sers pas.

### 5.10 Pilote graphique et fichiers Windows abîmés
1. Pilote : application NVIDIA ou AMD Adrenalin > Pilotes > Vérifier les mises à jour. DrDXT te dit si tu es en retard.
2. Fichiers Windows abîmés que `sfc` et `DISM` ne réparent plus (DrDXT te le dit) : Paramètres > Système > Récupération >
   « Réinstaller maintenant » (garde fichiers, applications et réglages ; long, redémarre le PC : hors partie ;
   **sans retour**, mais c'est la même version de Windows).

### 5.11 Écran
1. Branche l'écran sur la carte graphique, pas sur la carte mère.
2. Fréquence : DrDXT la met au maximum de l'écran ; vérifie que le câble (DisplayPort ou HDMI 2.1) la permet.
3. G-SYNC/FreeSync : activé dans le menu de l'écran et dans le pilote.
4. Si DrDXT dit « écran bridé » (une fréquence sous celle de l'écran) : menu de l'écran d'abord (version DisplayPort
   ou HDMI, overclock), puis un autre câble ou un autre port.

### 5.12 Ryzen X3D à deux blocs (7900X3D, 7950X3D, 9900X3D, 9950X3D) : le jeu sur le bloc à cache
1. BIOS, Advanced > AMD CBS > SMU : « CPPC » et « CPPC Preferred Cores » sur Enabled. C'est ce qui laisse Windows et le
   pilote du chipset choisir le bon bloc.
2. Pilote du chipset AMD à jour (site AMD, « Chipset Drivers ») : il apporte le service qui range le jeu sur le bloc à cache.
3. Ce service demande le plan d'alimentation Équilibré et la Xbox Game Bar activée pour reconnaître un jeu : garde-les
   (constat convergent de la communauté AMD et des testeurs ; AMD n'en publie pas de note technique que nous ayons trouvée).
   DrDXT ne propose jamais « Hautes performances » sur ces processeurs. Couper l'enregistrement Game DVR (ce que DrDXT
   fait) ne coupe pas la Game Bar.
4. Redémarre. Si un bloc reste parqué de travers, DrDXT le corrige lui-même (`fixit.x3d_ccd`).

### 5.13 Lien PCIe de la carte graphique : génération et largeur
1. DrDXT compare la largeur que la carte annonce (x16, ou x8 pour une RX 6600 ou une RTX 4060) à celle du lien en
   cours. Une carte sous sa largeur : remets-la dans le premier emplacement (le plus proche du processeur), vérifie
   qu'un SSD ne partage pas ses lignes, et qu'elle est enfoncée à fond. Le lien redescend au repos : juge en charge.
2. Génération : BIOS, Advanced > PCI Subsystem Settings, « PCIe x16 Link Speed » sur Auto (ou Gen4/Gen5 selon la carte).
   Gen3 forcé coûte des images sur une carte Gen4 à x8 de bus (RX 6600, RTX 4060) ; Auto suffit presque toujours.
3. Redémarre : le constat « lien PCIe » de DrDXT doit disparaître.

### 5.14 BIOS et microcode
1. Si DrDXT dit « BIOS ancien » ou « microcode antérieur au correctif » (Core 13e et 14e générations) : site du fabricant de
   la carte mère, page de ta carte, dernier BIOS. Lis sa note : c'est elle qui dit si le microcode d'Intel y est.
2. Mise à jour depuis le BIOS (M-Flash, EZ Flash, Q-Flash) avec le fichier sur une clé USB formatée en FAT32. Pas pendant
   un orage, onduleur si tu en as un : une coupure pendant l'écriture rend la carte muette. **Sans retour** la plupart
   du temps : un BIOS qui porte le correctif microcode refuse souvent de revenir en arrière.
3. Après : tes réglages (XMP/EXPO, limite de puissance) reviennent à « Auto », refais 5.1 et 5.3.

### 5.15 Disques
1. Jeux sur un disque dur : déplace-les sur un SSD (le lanceur sait déplacer un jeu installé : Steam > Propriétés >
   Fichiers installés > Déplacer ; Battle.net > roue > Déplacer).
2. Fichier d'échange sur un disque dur alors qu'un SSD est là : quand Windows est sur ce SSD et que la taille est gérée par
   le système, DrDXT rend sa gestion à Windows lui-même (Optimiser, effet au redémarrage, réversible). Sinon : Windows + R, `sysdm.cpl` > Paramètres système avancés >
   Performances > Paramètres > Avancé > Mémoire virtuelle > Modifier, décoche « Gérer automatiquement », puis « Taille
   gérée par le système » sur le SSD et « Aucun fichier d'échange » sur le disque dur ; redémarre.
3. Place libre du disque système sous 5 % : les mises à jour et le fichier d'échange n'ont plus de marge. Libère d'abord
   Téléchargements et la corbeille, puis Paramètres > Système > Stockage.
4. Erreurs non corrigées sur un disque : copie ce qui compte ailleurs D'ABORD, puis `chkdsk` ou l'outil du fabricant
   (**sans retour** : sur un disque qui lâche, la réparation peut finir d'abîmer ce qu'elle touche).

### 5.16 Windows et périphériques
1. Windows Update : Paramètres > Windows Update > Rechercher des mises à jour, puis Microsoft Store > Bibliothèque > Obtenir
   les mises à jour (les deux chemins se relancent à la main quand DrDXT dit qu'ils n'ont rien fait depuis longtemps).
   Pour Windows Update, DrDXT peut aussi relancer la recherche lui-même (« Chercher maintenant », sous le constat).
   Une mise à jour qui échoue ou reste « en attente » : Paramètres > Système > Dépannage > Autres utilitaires de
   résolution des problèmes > Windows Update > Exécuter, puis redémarre et relance la recherche.
2. Un périphérique dont le pilote est en échec (DrDXT nomme le code) : Gestionnaire de périphériques, clic droit, Mettre à
   jour le pilote, sinon Désinstaller puis redémarrer. DrDXT peut d'abord le relancer lui-même (« Le relancer », sous le
   constat), comme le Gestionnaire de périphériques.
3. Vieux pilotes graphiques gardés dans le magasin de Windows : DrDXT les nomme, et peut les retirer lui-même, un par un
   (« Retirer oemNN.inf », sous le constat) : il sauvegarde d'abord le paquet, et « Annuler » le remet. À la main,
   `pnputil /delete-driver oemNN.inf` en administrateur retire ceux qu'il a listés, **sans retour** : le paquet est
   supprimé. Le pilote en service n'est jamais dans la liste.
4. Plan d'alimentation « Économie d'énergie » pour jouer : sur un PC de bureau, DrDXT repasse en Équilibré lui-même (Optimiser,
   réversible). Sur un portable, où l'économie peut être voulue : Paramètres > Système > Alimentation, repasse en Équilibré.
5. Un réglage posé par DrDXT et défait depuis (mise à jour, autre programme) : relance l'optimisation, DrDXT ne le repose
   pas tout seul.
6. Doute sur la mémoire : Windows + R, `mdsched.exe`, le test tourne au redémarrage ; DrDXT lit le résultat. DrDXT
   peut aussi le demander lui-même pour le prochain redémarrage (« Le lancer au prochain démarrage », sous le constat),
   une seule fois, et l'annuler ; le redémarrage reste à toi.
7. Un service Windows qui plante ou redémarre sans cesse (DrDXT le nomme) : mets à jour ou réinstalle le logiciel à qui
   il appartient, ou désinstalle-le s'il ne sert plus. DrDXT ne touche pas aux services.

### 5.17 Mémoire saturée (PC de bureau comme PC de jeu)
1. DrDXT lit deux choses à l'instant du relevé (seuils DrDXT, pas une norme) : la mémoire libre, sous 10 % ce qui ne tient
   plus part sur le disque et tout ralentit (fenêtres en retard, souris qui accroche, saccades) ; et l'engagement, au-dessus
   de 90 % de sa limite la mémoire virtuelle (mémoire + fichier d'échange) est presque entièrement réservée et le prochain
   programme peut manquer de mémoire, même si rien ne pagine encore.
2. Du plus simple au plus durable : ferme ce qui ne sert pas (les onglets du navigateur comptent pour beaucoup) ;
   retire du démarrage les programmes que DrDXT nomme (5.9) ; laisse le fichier d'échange « géré par le système » sur
   un SSD (5.15) ; si c'est tous les jours, une barrette de plus (dans l'emplacement du second canal, 5.2) est le seul
   vrai remède.
3. Relance le scan après : le constat « mémoire saturée » doit passer à « pas de pression visible ».

### 5.18 PawnIO : la température et la limite de puissance du processeur, dans ses registres
1. Sans HWiNFO, Windows ne donne pas la température du processeur ni sa limite de puissance. PawnIO (pawnio.eu, pilote signé,
   code source public) les lit dans les registres du processeur. **DrDXT ne l'installe jamais** : c'est ton choix, c'est un
   pilote noyau. Installé dans son dossier par défaut (`Program Files\PawnIO`), DrDXT s'en sert pour LIRE la température, la
   limite de puissance et les raisons de freinage, si DrDXT est lancé en administrateur ; sinon ces constats restent « non
   lus », et c'est dit. Une seule ÉCRITURE passe par lui : la limite de puissance longue (PL1) quand tu acceptes ce levier,
   par le helper de DrDXT seulement, relue après coup, défaite au redémarrage (le BIOS remet sa valeur) ou en un clic.
2. Intel seulement pour l'instant ; sur un Ryzen, ces deux constats restent non lus.
3. Anti-triche : un pilote de lecture de registres est connu des anti-triches ; FACEIT bloque PawnIO (il refuse de lancer, il
   ne bannit pas) ; rien de connu côté Ricochet, Vanguard ou EAC, mais ce n'est pas mesuré : si tu joues en compétition,
   renseigne-toi avant. Retirer : `PawnIO_setup.exe -uninstall -silent`, ou Paramètres > Applications.

### 5.19 Surcouche DLSS et mode faible latence « Ultra » (carte NVIDIA) : des choix, pas des réparations
1. Surcouche DLSS : appli NVIDIA (pilotes 572 et plus), onglet Graphismes, un jeu, « Surcouche DLSS ». Le modèle DLSS 4
   (transformer) affine l'image dans les jeux DLSS 2 et 3 ; la génération d'images (RTX 40 et 50) multiplie les images
   comptées **en augmentant la latence** : à essayer jeu par jeu, et à juger à l'œil et à la main, pas au compteur
   d'images. DrDXT ne la met jamais en essai (le compteur d'images ne juge pas la netteté) : sous « Comment corriger » des
   réglages d'un jeu en DLSS, un bouton pose le dernier modèle (mise à l'échelle seulement) au profil pilote de ce seul jeu, et
   « Annuler » rend ce que le profil portait. Sans profil pilote pour le jeu, pas de bouton : DrDXT n'en crée pas.
   Source : NVIDIA, « DLSS 4 Multi Frame Generation » et « NVIDIA app global DLSS overrides » (nvidia.com/geforce/news).
2. Mode faible latence « Ultra » (panneau NVIDIA, « Mode faible latence ») : seulement pour les vieux jeux DX9 à DX11
   sans Reflex, quand la carte limite entre 60 et 100 images par seconde ; en DX12/Vulkan, seul Reflex dans le jeu compte ;
   hors de sa fenêtre, « Ultra » peut baisser les images ou la régularité. DrDXT pose déjà « On » (une image en file) au
   profil du jeu quand c'est utile. Source : Blur Busters, « NVIDIA Low Latency Mode - On or Off » (forums.blurbusters.com).

### 5.20 Batterie usée (portable)
1. DrDXT compare ce que ta batterie tient à pleine charge à ce qu'elle tenait neuve (les mêmes compteurs que
   `powercfg /batteryreport`). Sous 80 %, elle est usée : moins d'autonomie, et sur certains portables moins de puissance
   sur batterie.
2. En jeu, branche le chargeur. Pour la changer, vois le fabricant (garantie, pièce d'origine) : DrDXT n'y touche pas.

### 5.21 Carte graphique freinée par la chaleur
1. Pendant tes parties, le pilote NVIDIA dit à DrDXT quand la carte baisse sa fréquence pour se protéger de la chaleur.
   Au-delà de 5 % du temps, le constat le dit, avec la température la plus haute relevée.
2. Éteins le PC, débranche-le, et dépoussière les ventilateurs et le radiateur de la carte (bombe d'air, ventilateurs
   tenus pour qu'ils ne tournent pas trop vite).
3. Regarde le flux d'air du boîtier : de l'air frais qui entre devant ou dessous, de l'air chaud qui sort derrière ou
   dessus, rien qui bouche les entrées.
4. Rejoue : le constat doit baisser. Encore freinée : la courbe de ventilation de la carte (mode Expert de DrDXT, ou
   l'outil du fabricant) ou la pâte thermique de la carte (geste de réparateur).

### 5.22 Ventilateur qui s'arrête ou ne tourne plus
1. Pendant tes parties, DrDXT lit chaque ventilateur branché sur la carte mère (cartes MSI pour l'instant). Un ventilateur
   qui tournait et tombe à l'arrêt quelques secondes, ou qui tournait sur tes parties d'avant et ne tourne plus du tout,
   est dit, avec son numéro d'en-tête.
2. PC éteint et débranché : fais-le tourner à la main. Il doit tourner librement, sans frotter ; aucun câble ne doit
   toucher ses pales. Après un nettoyage, c'est la cause la plus fréquente.
3. Regarde son câble sur la carte mère : bien enfoncé, sur l'en-tête que DrDXT nomme (CPU_FAN pour celui du processeur).
4. Il frotte ou cale encore, propre et bien branché : son roulement est usé, change-le (un ventilateur de 120 mm coûte peu).
5. Celui du processeur tourne lentement pendant que le processeur se freine par la chaleur : sa courbe au BIOS est trop
   douce. Fais-la monter à 100 % dès 80 °C (réglage du ventilateur CPU, « Smart Fan » chez MSI).

<a id="sous-tension"></a>

### 5.23 Sous-tension du processeur (Intel) : moins de chaleur à la même fréquence
1. Page Processeur, « Trouver ma sous-tension » (jeu fermé, une dizaine de minutes) : DrDXT mesure la chaleur à la tension
   d'origine, puis baisse la tension des cœurs par paliers de −30 à −90 mV, chacun sous deux minutes de charge, et garde le
   dernier qui tient sans erreur matérielle. La tension d'origine est rendue à la fin ; un plantage la rend aussi (le
   redémarrage), et DrDXT dit au retour quel palier a planté.
2. « Ton BIOS refuse la sous-tension depuis Windows » : c'est « UnderVolt Protection », activée par défaut sur les Intel de 12e
   à 14e générations. Au BIOS, menu OC (sur MSI, la recherche du BIOS trouve « UnderVolt Protection »), passe-la à Disabled,
   enregistre et redémarre, puis relance la recherche. Elle sert à empêcher l'attaque Plundervolt contre SGX ; un
   processeur sans SGX (DrDXT le lit, et refuse tout sur un processeur qui l'a) n'a rien à protéger.
3. Pour garder la valeur trouvée : au BIOS, la tension des cœurs en mode adaptatif avec un décalage (offset) négatif de la
   valeur que DrDXT a dite. Rejoue : le docteur relit tes parties et dit si le processeur se freine moins.
4. Un écran bleu ou un jeu qui se ferme dans les jours qui suivent : remonte le décalage de 10 à 20 mV, ou remets-le à zéro.

## 6. Ce que DrDXT dit sans geste à faire

Certains constats sont des pistes, pas des gestes : les parties hors de tes heures d'activité, les logiciels installés
avant un incident, les arrêts dans la semaine qui a suivi un changement, les plantages d'un jeu, les erreurs matérielles
corrigées, le point de restauration qui contient le défaut, le type de vidage et la politique de démarrage, le test
mémoire en session. DrDXT les date et les rapproche, il n'en déduit rien : ils servent à savoir où regarder si un
incident revient.
