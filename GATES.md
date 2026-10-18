# GATES — ce qui bloque encore, et ce qui le débloque

Ce fichier dit ce qui n'est pas fini dans DrDXT : ce qui n'est pas prouvé, pas signé, pas validé
sur un matériel. Chaque ligne nomme le blocage et le geste qui le lève. Une ligne ne se ferme qu'avec
sa preuve (un test, un rapport de vérification rangé dans `tests/Shared/validations`, une mesure).

Les chiffres qui bougent ne s'écrivent pas ici ; leurs commandes, si :

- l'âge de chaque ligne ci-dessous : `python tools/gates/ages.py` ;
- les chemins cités par ce fichier et le README existent : `python tools/gates/liens.py` ;
- les tests que la CI ne lance pas (ils écrivent sur la machine), et pourquoi : `python tools/gates/hors_ci.py` ;
- l'avance d'un clone local sur le dépôt : `git rev-list --count origin/main..main`.

## Comment aider

Le plus utile : lancer « Vérifier DrDXT » sur ton PC et envoyer le rapport (bouton « Envoyer mon
rapport », ticket pré-rempli), ou « Exporter ma machine ». `tools/ranger_rapport.py` range un rapport
reçu là où les tests le rejouent, après avoir refusé tout ce qui identifie quelqu'un.

## Registre

| Ouvert le | Ce qui est bloqué | Ce qui le débloque |
|---|---|---|
| 09/10 | **Aucune Radeon n'a de rapport de validation en écriture.** Les RX 6000, 7000 et 9000 sont reconnues, lues, et rejouées en tests sur toute la gamme ; leurs réglages se posent sous garde, mais aucun n'a été vu écrit, relu et rendu sur une vraie carte. | un rapport « Vérifier DrDXT » d'une vraie Radeon |
| 09/10 | **Anti-Lag et FreeSync ne s'ouvrent que sur une lecture ADLX réelle.** Les tests couvrent la décision, pas la lecture d'une vraie carte. | le même rapport, ou un export de machine AMD |
| 09/10 | **Les cartes NVIDIA hors RTX 40 n'ont pas de rapport de validation.** Seule une RTX 40 a le sien : une RTX 4060. Toute la série RTX 40, de la 4050 à la 4090, est dite validée sur ce seul rapport ; les RTX 20 et 30 et les GTX 10 et 16 restent en lecture seule. | un rapport d'une RTX 20, 30 ou 50, ou d'une autre RTX 40 |
| 09/10 | **Aucun gain en jeu n'est encore prouvé.** Le juge compare des parties en alternance (réglage posé, puis rendu) ; il n'a pas encore assez de parties pour trancher au-dessus du bruit. | des parties normales jouées avec le relevé de fond |
| 09/10 | **Les binaires ne sont pas signés.** Windows SmartScreen prévient à l'installation. | la signature par SignPath Foundation (projet open source) |
| 09/10 | **Les guides BIOS ne sont vérifiés sur aucune carte mère.** Ils restent des guides (le joueur fait le geste), jamais une écriture. | des retours de joueurs, carte par carte |
| 09/10 | **Intel Arc : rien ne se règle.** La lecture IGCL attend une vraie carte pour être prouvée. | un export de machine Intel Arc |
| 17/10 | **La couverture ne décrit pas encore deux gestes.** La surcouche DLSS et la recherche de sous-tension du processeur existent, mais n'ont pas de règle dans `CouvertureDesLeviers` : `docs/COUVERTURE.md` ne dit pas sur quel matériel elles s'ouvrent. | une règle de couverture pour chacune, puis la régénération du document |
