# Ou vit le gain, et pourquoi il n'y en a pas toujours

Ce document repond a une question, posee le 07/09/2026 : **« on a un gain ou pas ? »**
Il est ecrit pour etre lu par un test : `TheGainStateOfTheArtHasNoUnsourcedClaimTests`.

**La regle du fichier, mecanique** : toute ligne du tableau porte soit une source (URL) ET une
date de consultation, soit la fourchette exacte `UNKNOWN`. Une affirmation chiffree sans source
ne compile pas le test.

## LA REPONSE, EN UNE LIGNE

**Un gain n'existe que la ou un defaut existe.** Sur une machine deja bien reglee, il n'y a rien a
gagner, et un produit qui promet un gain a tout le monde ment. Ce que le produit peut garantir a
CHAQUE client, ce n'est pas un gain : c'est un **verdict prouve** -- soit un gain mesure, soit
« ta machine est deja bien reglee, et voici la preuve ».

## AFFIRMATIONS

| id | affirmation | fourchette | classe | qualite | source | consultee le |
|---|---|---|---|---|---|---|
| G-01 | Faire tourner la memoire sur un seul canal coute des FPS moyens et davantage de 1 % low | 12 % de FPS moyen et 16 % sur les 1 % low sur 13 jeux en 1080p, jusqu'a 34 % dans le pire cas | SOURCE | SECONDAIRE | https://www.rankedram.com/guides/dual-channel-vs-single-channel | 2026-09-07 |
| G-02 | Laisser XMP/EXPO desactive laisse de la performance sur la table | annonce jusqu'a 20 a 30 % ; **aucune methodologie publiee** dans la source, a traiter comme un ordre de grandeur, pas comme un chiffre affichable | SOURCE | TERTIAIRE | https://www.xda-developers.com/pc-felt-like-it-had-half-the-ram-until-i-changed-this-setting/ | 2026-09-07 |
| G-03 | Un overclock GPU modeste sur une RTX 4060 rend peu en JEU, parce qu'Ada sort d'usine deja proche de sa courbe d'efficacite | environ 2 % en jeu avec +100 MHz de coeur et +20 % de limite de puissance (Cyberpunk 2077) | SOURCE | SECONDAIRE | https://www.tomshardware.com/news/to-no-ones-surprise-the-rtx-4060-is-an-unimpressive-overclocker | 2026-09-07 |
| G-04 | Une machine reelle rend moins qu'une machine de banc, a cause de ce qui tourne a cote | 5 a 15 % sous les conditions de banc propre ; sur un 6 coeurs et un jeu gourmand en threads, 5 % de CPU perdu coute 10 a 15 FPS de 1 % low ; **fonctionnellement nul** sur une machine large et un scenario GPU-bound | SOURCE | TERTIAIRE | https://betterfps.com/blog/do-fps-boosters-actually-work-tested | 2026-09-07 |
| G-05 | Part du parc de PC de jeu reellement concerne par ces defauts (XMP off, simple canal) | UNKNOWN | NON CHIFFRE | TERTIAIRE | recherche du 2026-09-07 : aucune enquete chiffree trouvee, seulement des affirmations qualitatives (« l'erreur la plus courante ») | 2026-09-07 |

## CE QUE CES CHIFFRES IMPLIQUENT POUR CE PRODUIT

**Notre plancher de bruit en jeu est de 9,9 %** (banc du 01/09/2026, `docs/INSTRUMENTS.md`).
Confronte aux lignes ci-dessus, il decide de ce que le produit a le droit de dire :

| levier | ordre de grandeur sourcé | mesurable en jeu par NOUS ? |
|---|---|---|
| simple -> double canal (G-01) | 12 a 34 % | **OUI**, largement au-dessus du plancher |
| XMP off -> on (G-02) | ordre de grandeur seulement | oui sur le DEBIT memoire (+78 % mesure ici), pas sur les FPS |
| overclock GPU +100 MHz (G-03) | environ 2 % | **NON — cinq fois sous notre plancher** |
| bruit de fond (G-04) | 5 a 15 %, tres dependant de la machine | a la limite, et seulement sur les machines contraintes |

**La consequence la plus dure, et elle est deja verifiee chez nous** : le gate P3 du 01/09 a
mesure « ne rien faire » a **+8,3 FPS** contre **+7,0 FPS** pour l'overclock. Ce n'etait pas un
accident de mesure -- c'est exactement ce que G-03 predit. **Le produit n'a donc pas le droit
d'annoncer un gain de FPS pour un offset de coeur.**

## CE QUE CE DOCUMENT NE DIT PAS

- Il ne dit pas combien de clients ont un de ces defauts : c'est `G-05`, et c'est `UNKNOWN`.
  Tant que ce nombre n'existe pas, **aucune promesse de la forme « X % des PC gagnent Y »** ne
  peut etre ecrite nulle part, ni dans le produit, ni dans une page de vente.
- Il ne dit pas ce que TA machine gagne : seule une mesure avant/apres sur ta machine le dit.
