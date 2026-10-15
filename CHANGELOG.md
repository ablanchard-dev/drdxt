# Changements

## 0.9.1 (en cours)

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
