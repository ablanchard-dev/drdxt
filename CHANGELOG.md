# Changements

English version of the coming changes: [CHANGELOG.en.md](CHANGELOG.en.md).

## 0.9.2 (non publiée)

Pour le joueur :

### Sûreté de la machine
- Un réglage que DrDXT pose est noté avant d'être écrit : si DrDXT s'arrête avant d'avoir gardé son origine, elle est
  rendue au prochain geste en administrateur, ou au démarrage de Windows si le service est installé, jamais pendant
  une partie.
- Aucune invite de Windows ne s'ouvre sans un geste de ta part ; le relevé de fond des parties tourne sans droits
  d'administrateur.
- Une écriture que DrDXT ne peut pas noter d'avance est refusée, plutôt que faite sans filet.
- Les programmes ralentis pendant une partie retrouvent toujours leur priorité, même si l'option est coupée puis
  remise, et à la fermeture de DrDXT.
- Les fichiers de DrDXT ne s'écrivent jamais à travers un lien posé d'avance, et une réécriture passe par un fichier
  neuf, jamais en place.
- Mise à jour : la nouvelle version est annoncée par un fichier signé, le téléchargement ne part que sur ton clic, et
  jamais depuis une App lancée en administrateur.
- Installeur : une mise à jour arrête proprement les programmes de DrDXT et retire les anciennes copies de PresentMon.
- Désinstallation : les réglages gardés reviennent à leur origine et seuls les fichiers de DrDXT sont retirés ; une
  carte NVIDIA revient au ventilateur automatique et à un décalage de 0 MHz, même un décalage posé par un autre outil ;
  la sous-tension AMD n'est pas encore rendue, et c'est dit.
- Fichier d'échange : jamais coupé par la désinstallation ; déplacé sur un SSD, il revient à sa place, sauf des tailles
  fixées à la main, dites au rapport.
- Ventilateur réglé à vitesse fixe : il rend la main à la courbe de la carte dès 80 °C, et dans les deux minutes si
  DrDXT ne répond plus.
- Carte AMD : la remise à l'usine ne part que sur ton clic.
- Limite de puissance et sous-tension du processeur : bornes revérifiées au moment d'écrire ; aucune hausse de limite
  tant qu'aucune température de partie n'a été relevée.
- Le réglage de la carte est refusé quand un autre outil y a déjà posé un décalage, ou quand ce décalage n'a pas pu
  être lu.
- Plafond d'envoi pendant une partie : seulement pour un jeu reconnu ; effacé au redémarrage de Windows, reposé par le
  service à la partie suivante tant que la protection reste allumée.
- Un antivirus ou un contrôle parental n'est jamais coupé au démarrage.
- Les identifiants Windows ne vont qu'à un proxy que tu as configuré, jamais à un proxy découvert sur le réseau.
- Les lecteurs de stabilité, de pilotes, de Defender et de BitLocker ne peuvent rien écrire.
- Dossier des témoins d'incident durci ; clé de signature des mises à jour de développement tournée.

### Honnêteté des messages
- Un échec se dit en phrase claire, jamais par le message technique, et sa raison n'affirme rien sur l'état de la
  machine qu'elle ne sait pas.
- Quand la partie administrateur ne répond plus après un envoi, l'App dit « peut être posé ou non », plus « échec ».
- Ce qui n'a pas pu être lu reste « non lu », jamais une valeur supposée : fréquence de l'écran, VRR, plafond
  thermique sans température de partie.
- À l'inverse, un réglage jamais écrit se lit comme le défaut de Windows ou du pilote, plus comme illisible : mode Jeu,
  réglages NVIDIA.
- Un réglage déjà à sa valeur est dit déjà réglé ; un retour arrière refusé est dit à moitié posé ; une inscription ou
  un retrait ratés le disent.
- La latence PC est dite estimée ; un seul palier de sous-tension tenu ne promet aucune marge ; le mode Expert de la
  carte dit la durée réelle de son test au lieu de « Stable ».
- Les nombres et les dates suivent la langue de l'App ; les textes anglais prennent des guillemets anglais.
- L'information d'expert sans problème est repliée dans « Détails techniques » sous chaque page ; l'étiquette
  « recommandation » est retirée.
- Le README dit exactement ce qui reste après la désinstallation.
- « Envoyer mon rapport » dit que le ticket s'ouvre vide.
- « Quoi de neuf » montré une fois après une mise à jour de DrDXT ; paquet d'aide en un clic, montré avant d'être
  écrit.

### Mesure
- Essai en alternance en blocs A-B-B-A, avec un test à blanc avant tout verdict ; un réglage de latence se juge sur la
  latence de ses parties.
- La latence d'affichage ne compte que les moments joués ; les menus ne comptent plus.
- Courbe des temps de trame de chaque mesure, avec sa médiane ; les pics au-dessus du 99e centile sont marqués.
- Deux façons différentes de calculer le 1 % low ne se comparent plus ; « pas comparable » ne désigne plus à tort la
  partie d'avant.
- Le coût d'un overlay se juge sur trois parties de chaque côté, comme les autres réglages.
- Parties : part d'images composées, latence PC, mémoire du jeu hors de la carte ; bilan des sessions d'OBS.
- Test de ta ligne sous charge, sur un clic, contre un serveur nommé à l'avance, plafonné et annulable.
- La jauge du scan suit la vraie durée des tests ; les journaux de Windows ne sont lus qu'une fois par scan.

### Diagnostic
- Stabilité : écrans bleus comptés par trois sources (code d'arrêt, rapport de Windows, vidages), arrêts brutaux par le
  seul événement Kernel-Power 41, erreurs matérielles avec le cœur ou le lien PCIe qui les a relevées, et ce que
  Windows a noté juste avant un plantage gardé sans rien à lancer.
- Santé des SSD NVMe lue sur le disque lui-même ; firmware de SSD que son fabricant a corrigé depuis, signalé avec son
  bulletin.
- Test mémoire de Windows : un arrêt pendant le test compte comme un échec, et le test ne prend jamais plus que la
  moitié de la mémoire libre.
- Conseils Curve Optimizer et profil mémoire (XMP/EXPO) retenus sur toute machine instable ; plus de Curve Optimizer
  proposé à un X3D de la série 5000.
- Pilotes : inventaire complet, plancher de pilote que chaque jeu fixe, installation bloquée jugée comme Windows,
  pilote graphique 32 bits manquant (lanceur qui reste noir), pilote RTCore signalé.
- Mémoire saturée jugée sur la pagination mesurée, pas sur la seule mémoire libre.
- Processeur : temps passé en C1, C2 et C3, coût de l'horloge haute résolution, cœur le plus occupé par les pilotes,
  protections Spectre et Meltdown dites sans jamais proposer de les couper.
- Ryzen : génération lue par CPUID ; Curve Optimizer et échelle PBO lus au BIOS ; timings appliqués et butées PPT,
  TDC, EDC et thermique prêts, en attente du module de lecture AMD.
- BIOS : les réglages qui ne sont plus à leur valeur d'usine ; ceux d'une carte AMD nommés, carte par carte.
- Régressions publiées : DrDXT prévient quand une mise à jour, un pilote ou ton processeur y figure ; ses tables
  arrivent signées, sans changer de version de l'App.
- Réglages qu'un autre outil a pu laisser dans le registre, avec leur date.
- Resizable BAR prouvé par la plage mémoire allouée à chaque carte ; sur un X3D, les cœurs du bloc à cache mal parqués
  sont vus.
- Entrées : chaque souris, clavier et manette avec son rôle et sa liaison, couches logicielles intercalées, cadence
  demandée au bus, rapports réellement envoyés.
- Affichage : superpositions matérielles coupées, écrans dupliqués à deux fréquences, encodage et profondeur de
  couleur écran par écran.
- Réseau : vitesse ou duplex forcés, programmes qui occupent la montée, type de NAT lu à la box (lecture seule).
- Frise : jeux mis à jour et pilotes d'affichage changés depuis le scan précédent.
- Avant de jouer : dernier vrai redémarrage, mémoire vidéo déjà prise, audio virtuel, disque chiffré.

### Maintien
- Pause du maintien : un interrupteur dans l'App suspend la remise des réglages au démarrage ; un fichier de pause
  douteux vaut pause, et la désinstallation l'efface.
- Le service de démarrage attend la fin d'une partie pour sa passe quotidienne.
- Après une mise à jour de Windows, tout ce que DrDXT a posé est relu et le bilan est dit.
- « Vérifier DrDXT sur mon PC » se suit en direct, s'arrête entre deux réglages après confirmation, relit chaque
  réglage après l'avoir posé, et ne touche pas un réglage tenu par l'essai en alternance.
- Réglages suivis : une lecture ratée ne fait plus perdre l'historique, et l'App et le relevé de fond ne se gênent
  plus.
- Une seule App par session : un second lancement montre la fenêtre ouverte.

## 0.9.1 (15/10/2026)

Pour le joueur :
- Les parties sont mesurées en entier, menus et autres fenêtres à part : FPS et 1 % low sur toutes les images, minutes
  vraiment jouées, puissance, température du processeur chaque seconde (cœur le plus chaud et cœur 0), ventilateurs,
  programmes ouverts à côté.
- Le docteur explique un ralentissement en jeu par les secondes freinées et leur raison (limite de courant, limite de
  puissance, chaleur, PROCHOT), pas par la température seule, et nomme le réglage BIOS en cause (CPU Current Limit,
  Lite Load) avec la valeur lue.
- Les réglages BIOS lus en NVRAM (limites de puissance, limite de courant, Lite Load, CEP) sont datés et suivis d'un
  démarrage à l'autre : un réglage changé devient une comparaison des parties d'avant et d'après.
- « Tester le refroidissement » (page Processeur) : une charge courte, la température chaque seconde, arrêt
  automatique ; le docteur dit si le refroidissement tient la limite de puissance.
- Levier « plafond de puissance contre la chaleur » : une limite PL1 posée à chaud, réversible, jugée sur les parties
  enregistrées ; jamais de hausse sur un Core de 13e ou 14e génération sans le microcode qui protège la tension.
- « Trouver ma sous-tension » (Intel) : paliers descendants avec test de charge, retour automatique à l'origine à la
  fermeture ou après un plantage, refus expliqué quand le BIOS (UnderVolt Protection) ou le processeur (SGX) s'y
  oppose ; la valeur stable se reporte ensuite au BIOS, guidée pas à pas (étape 5.23).
- Chaque constat porte sa façon de corriger, pas à pas ; ce que DrDXT sait faire lui-même y devient un bouton, avec
  Annuler : couper un programme au démarrage, quitter le plan Économie d'énergie sur un PC de bureau, rendre à Windows
  un fichier d'échange posé sur un disque dur, programmer le diagnostic de mémoire, relancer un périphérique en panne,
  retirer un ancien pilote d'affichage, relancer une recherche Windows Update restée vieille.
- Jeux DLSS : un bouton pose le dernier modèle DLSS au profil pilote du seul jeu, avec Annuler.
- Jeux Unreal : éclairage global et reflets lus dans le fichier du jeu ; en épique, conseil de passer en élevé selon le
  goulot de la dernière partie.
- Courbe du ventilateur processeur lue au BIOS et suivie comme un réglage daté.
- Le jeu au premier plan l'emporte sur un jeu connu resté ouvert sur un autre écran ; un nom de jeu avec une espace
  n'est plus écarté.
- Historique de la machine : frise à largeur de lecture, les FPS d'une partie sous le nom du jeu.
- Ventilateurs de la carte mère lus sur la puce Super I/O (NCT6687 pour l'instant), repli HWiNFO ; santé des disques ;
  usure de la batterie sur portable.
- L'App non élevée lit l'état du processeur par le service ; le relevé de fond tourne en tâche planifiée élevée, sans
  rien à lancer.
- Tout le docteur existe en anglais et en français ; README et CONTRIBUTING en anglais d'abord ; ARCHITECTURE.md pour
  les contributeurs.
- Interface : page de résultat recentrée, un seul chrome (Optimiser), étiquettes lisibles sur fenêtre étroite,
  animations resserrées.
- Retirés : le score sur 100, le canal de profils, le code que rien n'atteignait.

Sécurité :
- Tube du service durci (pas de squat, clients muets bornés), séparateur des requêtes signées échappé, manifeste relu
  une fois par passage.
- Retour automatique des écritures NVAPI que le pilote arrondit ou refuse ; origines HKCU refusées à la désinstallation
  sous un autre compte ; état BCD lu avant écriture et rendu s'il n'est pas confirmé.

## 0.9.0 (09/10/2026)

Pour le joueur :
- Chaque page dit ce que DrDXT y fait, réglage par réglage : posé, à poser, déjà bon, pas lu, ou pas sur ton
  matériel, et pourquoi. La même liste, par type de PC : [docs/COUVERTURE.md](docs/COUVERTURE.md).
- « Vérifier DrDXT sur mon PC » : chaque réglage posé, relu, rendu et relu encore, avec ton accord ; le rapport
  sert de preuve.
- Mode Expert de la carte graphique (page Graphique) : décalage de fréquence sur ton accord, testé, gardé tant que
  DrDXT est ouvert, rendu tout seul s'il ferme ou plante.
- Cartes AMD : FreeSync lu au pilote. Cartes NVIDIA : file de rendu à une image pour les jeux sans Reflex.
- Portables : boost sur secteur, file de rendu et plafond sous VRR ouverts.
- Wi-Fi : signal, bande, canal et génération, lus sans demander la localisation.

Sécurité :
- Le helper administrateur s'arrête après 15 minutes sans connexion ni bail ; les baux sont plafonnés.
- Les valeurs de ta session s'écrivent avec ton jeton, plus par le helper.
- Les limites connues sont écrites dans [docs/SECURITE.md](docs/SECURITE.md).
