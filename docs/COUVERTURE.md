# Ce que DrDXT fait, selon le PC

Généré depuis le code par `CouvertureDocTests` : ne pas modifier à la main. « possible » = le levier existe
sur ce matériel ; il n'est proposé que si la machine lue en a besoin, et mesuré en jeu quand il touche les
images. Sinon, la raison.

| Levier | Page | Fixe Intel + NVIDIA | Fixe Ryzen X3D + AMD | Fixe, carte Intel intégrée | Portable NVIDIA | Console portable |
|---|---|---|---|---|---|---|
| Écran à sa vraie fréquence (`fixit.ecran_frequence`) | graphique | possible | possible | possible | possible | possible |
| Courbe de ventilation GPU (`gpu.fan_curve`) | graphique | mode Expert, sur ton accord | Réglage du pilote NVIDIA : pas de carte NVIDIA ici. | Réglage du pilote NVIDIA : pas de carte NVIDIA ici. | Portable ou console : le ventilateur est piloté par le constructeur, DrDXT n'y touche pas. | Portable ou console : le ventilateur est piloté par le constructeur, DrDXT n'y touche pas. |
| Limite de puissance GPU (`gpu.power_limit`) | graphique | possible | possible | Pas de carte graphique dédiée ici. | Portable : les fréquences de la carte sont verrouillées par le pilote. | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Décalage d'horloge GPU testé (`gpu.undervolt`) | graphique | mode Expert, sur ton accord | mode Expert, sur ton accord | Pas de carte graphique dédiée ici. | Portable : les fréquences de la carte sont verrouillées par le pilote. | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Performances GPU maximales (pilote) (`latency.prefer_max_perf`) | graphique | possible | Réglage du pilote NVIDIA : pas de carte NVIDIA ici. | Réglage du pilote NVIDIA : pas de carte NVIDIA ici. | Portable : ce réglage touche aussi la batterie et la chauffe. | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Enregistrement Game DVR en arrière-plan désactivé (`latency.gameDvrBackgroundOff`) | latence | possible | possible | possible | possible | possible |
| Mode Jeu de Windows (`latency.gameModeOn`) | latence | possible | possible | possible | possible | possible |
| Planification GPU à accélération matérielle (`latency.hagsOn`) | latence | possible | possible | possible | possible | possible |
| Accélération du pointeur coupée (`latency.mouse_acceleration_off`) | latence | possible | possible | possible | possible | possible |
| NVIDIA Ultra Low Latency (pilote) : DirectX 9 a 11 uniquement (`latency.reflex`) | latence | Reflex s'active dans le jeu ; DrDXT ne fait que défaire ce qu'une ancienne version avait écrit. | Reflex s'active dans le jeu ; DrDXT ne fait que défaire ce qu'une ancienne version avait écrit. | Reflex s'active dans le jeu ; DrDXT ne fait que défaire ce qu'une ancienne version avait écrit. | Reflex s'active dans le jeu ; DrDXT ne fait que défaire ce qu'une ancienne version avait écrit. | Reflex s'active dans le jeu ; DrDXT ne fait que défaire ce qu'une ancienne version avait écrit. |
| Limite de la file de rendu (`latency.render_queue_cap`) | latence | possible | possible | Pas de carte graphique dédiée ici. | possible | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Optimisations pour les jeux en fenêtre (VRR) (`latency.vrrFlipModelOn`) | latence | possible | possible | possible | possible | possible |
| VRR + Vsync + cap d'images (`latency.vrr_vsync_cap`) | latence | possible | possible | Pas de carte graphique dédiée ici. | possible | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Fichier d'échange sain (`fixit.sane_pagefile`) | memoire | possible | possible | possible | possible | possible |
| Limite de puissance du processeur montée (`fixit.cpu_power_limit`) | processeur | possible | Registres du processeur Intel : pas de processeur Intel ici. | possible | Portable : ce réglage touche aussi la batterie et la chauffe. | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Boost du processeur rendu (`fixit.mode_boost`) | processeur | possible | possible | possible | possible | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Mode d'alimentation sain (`fixit.power_plan`) | processeur | possible | Pas sur un Ryzen X3D à deux blocs : d'après la communauté AMD et les testeurs (aucune note AMD trouvée), son pilote range le jeu sur le bloc à cache sous le plan Équilibré seulement. | possible | Portable : ce réglage touche aussi la batterie et la chauffe. | Console portable : ses profils d'énergie et de carte sont verrouillés. |
| Correctif de routage CCD X3D (`fixit.x3d_ccd`) | processeur | Seulement pour un Ryzen X3D à deux blocs de cœurs. | possible | Seulement pour un Ryzen X3D à deux blocs de cœurs. | Seulement pour un Ryzen X3D à deux blocs de cœurs. | Seulement pour un Ryzen X3D à deux blocs de cœurs. |
| Heures d'activité de Windows Update sur tes heures de jeu (`fixit.heures_actives`) | windows | possible | possible | possible | possible | possible |
