# À valider sur AMD

English version: [VALIDATION-AMD.md](VALIDATION-AMD.md).

DrDXT n'a pas de machine AMD. Tout ce qui touche un processeur Ryzen ou une carte Radeon est écrit et testé sur des
données, **jamais vérifié sur une vraie machine AMD**. Une génération ne passe « validée » qu'avec un rapport réel,
archivé dans `tests/Shared/validations` (voir [CONTRIBUTING.fr.md](../CONTRIBUTING.fr.md) §1). Pour une carte, c'est
`GpuTuningCoverage.ValideesEnVie`, et une garde refuse une architecture validée sans son rapport ; les lectures du
processeur suivront la même règle, génération par génération.

Chaque ligne ci-dessous dit ce que fait le chemin, son état, comment le tester et ce que le rapport doit montrer. Le
test se fait avec les zips de la [Release](https://github.com/ablanchard-dev/drdxt/releases) :
`DrDXT-Validation-AMD.zip` (`VALIDER.cmd`, qui pose, relit, rend et relit chaque réglage) et
`DrDXT-Rapport-Lecture-Seule.zip` (`DrDXT-Test.exe`, qui ne modifie rien). Le rapport part par une issue
« Validation AMD ».

## Les machines qui manquent

Une génération à la fois suffit ; chacune compte :

- Processeur : Zen 2 (Ryzen 3000), Zen 3 (Ryzen 5000), Zen 4 (Ryzen 7000), Zen 5 (Ryzen 9000), et un X3D à deux
  blocs (7950X3D, 9950X3D) pour le routage du cache.
- Carte : RDNA 2 (RX 6000), RDNA 3 (RX 7000), RDNA 4 (RX 9000).

## Le protocole, pour chaque chemin

1. Fermer tous les jeux. Noter le processeur, la carte, la version du pilote AMD et la version de DrDXT.
2. Avant : relever la valeur dans l'outil de référence (logiciel AMD Adrenalin pour la carte, HWiNFO pour les capteurs,
   Ryzen Master pour le processeur, ZenTimings pour la mémoire).
3. Lancer le zip. Pour une écriture, `VALIDER.cmd` ; pour une lecture, `DrDXT-Test.exe`.
4. Après : relever de nouveau la valeur dans l'outil de référence ; elle doit être celle d'avant.
5. Refaire une fois avec HWiNFO ouvert pendant le test (deux outils qui lisent en même temps ne doivent rien corrompre).
6. Refaire une fois sans droits administrateur (refuser la demande de Windows) : chaque lecture qui en a besoin doit se
   dire « non lue », rien ne doit planter.

Le rapport doit porter, pour une écriture, la ligne `rendu        : oui` sans « PAS REMIS D'ORIGINE » ; pour une lecture, la valeur
lue par DrDXT à côté de celle de l'outil de référence.

## Écritures sur la carte (pilote AMD, ADLX)

| Chemin | Ce qu'il fait | État | Test | Le rapport montre |
|---|---|---|---|---|
| Limite de puissance (`gpu.power_limit`) | baisse la limite de puissance de la carte par le pilote | pas proposé sur AMD : l'unité de la plage d'ADLX (GetPowerLimitRange), % du TDP ou décalage, n'est pas relevée sur une vraie carte | `VALIDER.cmd` | rien n'est écrit : la limite est seulement lue |
| Anti-Lag (`latency.render_queue_cap`) | active Anti-Lag pour raccourcir la file de rendu | non validé sur AMD réel | `VALIDER.cmd` | Anti-Lag posé, relu, rendu, relu ; Adrenalin d'accord |
| Vsync du pilote et plafond FRTC (`latency.vrr_vsync_cap`) | sur un écran à fréquence variable, Vsync « toujours activée » et un plafond d'images sous la fréquence de l'écran | non validé sur AMD réel | `VALIDER.cmd`, écran à fréquence variable branché sur la carte | les deux réglages posés puis rendus à leur valeur d'origine ; Adrenalin d'accord |
| Fréquence maximale testée (`gpu.undervolt`, mode Expert) | essaie une fréquence maximale plus basse, la garde seulement si elle tient | non validé sur AMD réel | `VALIDER-FREQUENCE.cmd`, à part de `VALIDER.cmd` (qui n'écrit jamais de fréquence) | la fréquence maximale d'origine, celle baissée de 50 MHz, puis l'origine relue ; Adrenalin d'accord avant et après |

## Écritures sur le processeur

| Chemin | Ce qu'il fait | État | Test | Le rapport montre |
|---|---|---|---|---|
| Routage CCD X3D (`fixit.x3d_ccd`) | démarre l'optimiseur 3D V-Cache d'AMD et le mode Jeu automatique, pour que le jeu tourne sur le bloc à cache | non validé sur AMD réel | pas encore dans le kit | à venir |
| Mode boost (`fixit.mode_boost`), plan d'alimentation (`fixit.power_plan`) | réglages Windows, aussi sur un Ryzen ; le plan n'est pas proposé sur un X3D à deux blocs | non validé sur AMD réel | `VALIDER.cmd` | posé, relu, rendu, relu |

## Lectures de la carte (ADLX)

| Chemin | Ce qu'il fait | État | Test | Le rapport montre |
|---|---|---|---|---|
| Réglages du pilote | lit SAM, Anti-Lag, Chill, FRTC, Vsync et Enhanced Sync, et Radeon Boost, Image Sharpening, RSR et AFMF, que DrDXT n'écrit jamais | non validé sur AMD réel | `DrDXT-Test.exe --lire`, dans une invite de commandes ouverte dans le dossier du zip | chaque valeur lue par DrDXT, et la même dans Adrenalin |
| Mesures de la carte | lit, à l'instant, la température de la carte, sa consommation, sa fréquence graphique, son point chaud et la vitesse de son ventilateur | non validé sur AMD réel | `DrDXT-Test.exe --lire`, dans la même invite | les lignes `Température`, `Consommation`, `Fréquence GPU`, `Point chaud` et `Ventilateur`, et HWiNFO au même moment |
| FreeSync | lit, pour chaque écran branché sur la carte, si FreeSync est pris en charge et activé | non validé sur AMD réel | `DrDXT-Test.exe --lire`, dans la même invite | une ligne `FreeSync (écran N, nom)` par écran, et la même chose dans l'onglet Affichage d'Adrenalin |

## Lectures du processeur

| Chemin | Ce qu'il fait | État | Test | Le rapport montre |
|---|---|---|---|---|
| Génération et fTPM | reconnaît la génération par CPUID ; pour les saccades liées au fTPM (article AMD PA-410), dit un BIOS « à vérifier » d'après sa date | non validé sur AMD réel | `DrDXT-Test.exe` (étape « Docteur : tous les constats ») | la génération lue, et le processeur exact (Ryzen Master) |
| Réglages du BIOS AMD nommés | lit les réglages du BIOS (dont le Curve Optimizer et l'échelle PBO) et ce qui s'écarte des valeurs d'usine | non validé sur AMD réel | pas encore dans le kit | à venir : les réglages lus, et les mêmes dans le BIOS |
| Température (Tctl et par CCD) | registres thermiques, par les modules AMD de PawnIO (embarqués, en lecture seule) | non validé sur AMD réel | `DrDXT-Test.exe --lire`, PawnIO installé | les lignes `Tctl` et `CCD` à moins de 1 °C de HWiNFO |
| Cause matérielle des redémarrages | le registre de redémarrage du processeur (S5_RESET_STATUS), lu sans l'effacer | non validé sur AMD réel | `DrDXT-Test.exe --lire`, PawnIO installé | la ligne `S5 (reset)` après un arrêt propre, puis après un appui long sur le bouton (bit 1) |
| Timings mémoire appliqués | les timings que le contrôleur mémoire applique vraiment, canal par canal | non validé sur AMD réel | `DrDXT-Test.exe --lire`, PawnIO installé | les registres bruts `Timings canal`, et une capture de ZenTimings de la même session |
| Limites PPT, TDC, EDC et thermique | la table de puissance du processeur, en jeu, par le module RyzenSMU de PawnIO (embarqué, en lecture seule) | non validé sur AMD réel | pas encore dans le kit | valeurs et limites, et les mêmes dans HWiNFO ou Ryzen Master |
| Erreurs matérielles corrigées (MCA) | les erreurs que le processeur a corrigées | en attente du module | à venir | les erreurs lues, et les mêmes dans l'Observateur d'événements (WHEA) |

Le Curve Optimizer par cœur n'est pas lu dans le processeur : il faudrait une commande hors de la liste fermée de
lecture. Il reste lu dans le BIOS.
