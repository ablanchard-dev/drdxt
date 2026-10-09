# Quel protocole rend une vraie partie mesurable ?

**Mesuré le 07/09/2026** en rejouant les captures Warzone du 01/09 (`tools/p3`, i7-14700F /
RTX 4060) à travers le code de production : découpage en passes, score `Competitive`
(0,35 × moyenne + 0,65 × 1 % low), puis `SceneResolution.Probe`.

La question est celle du produit : **une partie libre dérive assez pour masquer un gain de 1–3 %.**
Quel découpage donne la meilleure résolution pour le temps qu'on demande au joueur ?

## Ce que les données disent

Résolution obtenue selon la durée de passe, sur les huit captures, à temps de jeu comparable :

| capture | 10 s | 15 s | 20 s | 30 s | 45 s | 60 s |
|---|---|---|---|---|---|---|
| 220100 | **±2,30 %** | ±3,29 % | ±3,83 % | ±5,30 % | ±32,3 % | — |
| 220759 | **±2,09 %** | ±2,50 % | ±2,69 % | ±2,87 % | ±3,85 % | ±4,50 % |
| 221004 | **±3,27 %** | ±4,03 % | ±4,51 % | ±6,69 % | ±9,24 % | ±11,7 % |
| 231412 | **±3,08 %** | ±3,76 % | ±4,78 % | ±6,79 % | ±44,7 % | — |
| 232708 | ±3,58 % | **±3,51 %** | ±4,90 % | ±3,84 % | ±26,9 % | — |
| 233700 | **±4,75 %** | ±5,67 % | ±7,17 % | ±12,1 % | ±20,0 % | ±33,5 % |
| 230602 | **±10,4 %** | ±15,2 % | ±17,3 % | ±32,9 % | ±206 % | — |
| 230927 | **±10,5 %** | ±13,6 % | ±20,0 % | ±23,2 % | ±142 % | — |

**Les passes courtes résolvent mieux, systématiquement.** Médiane à 10 s : ≈ 3,4 %. À 20 s :
≈ 4,8 %. À 45 s : ≈ 30 %. Sept captures sur huit sont meilleures à 10 s qu'à toute autre durée.

## Pourquoi — et pourquoi le produit croyait le contraire

Le produit utilisait **60 s par passe**, sur la foi d'une mesure du 01/09 : « 3 × 10 s → ±33,3 FPS,
seuil 22 % ; 3 × 60 s → ±2,12 FPS, seuil 1,6 % ». Cette mesure comparait **trois** passes courtes à
**trois** passes longues — donc six fois moins de temps de jeu d'un côté. Elle confondait la durée
de passe avec le temps total.

À temps de jeu **égal**, l'arithmétique va dans l'autre sens : la marge inter-passes vaut
`t₉₅(n−2) × σ / √n`. Passer de 3 à 17 passes fait chuter `t₉₅` de 4,30 à 2,13 **et** multiplie `√n`
par 2,4 — un facteur ~4,8 sur la résolution, que l'augmentation de σ sur des passes plus courtes ne
compense pas.

Contrepartie réelle : à 10 s, le 1 % low ne porte que sur ~14 frames (à 140 fps). Le percentile
devient fragile, et la mesure du 01/09 notait aussi que « 10 s ne laisse pas le jeu se stabiliser ».

## Le choix retenu : 15 s

À 140 fps, une passe de 15 s porte ~2 100 frames et son 1 % low ~21 frames — assez pour que le
percentile tienne. La résolution y reste à moins de 10 % du meilleur obtenu à 10 s, et le nombre
de passes reste raisonnable pour un joueur.

**NON CHIFFRÉ** : 15 s est un compromis entre trois choses mesurées (résolution, fiabilité du
percentile, temps joueur), pas un optimum démontré. Il est écrit ici pour être discuté.

## Ce que ça change concrètement

Une session de **7 passes × 15 s ≈ 1 min 45 de jeu** obtient sur cette machine une résolution de
l'ordre de **±3–4 %** au lieu de **±11 %** avec l'ancien découpage à 60 s pour un temps comparable.

C'est encore au-dessus de l'objectif (démontrer +1–3 %), mais le produit passe d'« incapable » à
« proche ». Les leviers restants sont ailleurs : une scène plus stationnaire (benchmark intégré,
zone d'attente, menu d'entraînement) plutôt qu'une partie libre.

## Ce qui reste vrai malgré tout

**Deux captures sur huit dérivent quelle que soit la durée** (230602, 230927 — celles qui
contiennent une mort). Aucun découpage ne sauve une scène qui change vraiment. C'est
`SceneVerdict.DriftDominant` qui doit alors parler, et le produit doit refuser de tester plutôt que
d'attribuer le temps écoulé au réglage.

## Ce qui n'a PAS été essayé, et ne le sera pas

Aucune injection d'input, aucune macro, aucune automatisation de gameplay, aucun hook. Le protocole
reste : le joueur joue, DrDXT observe.

---

# Deuxième mesure — 07/09/2026 au soir : quelle GRANDEUR sait décider ?

La première mesure ci-dessus répondait à « quelle durée de passe ». Elle laissait entière la
question qui bloquait vraiment le produit : en session Warzone réelle, la moyenne résolvait
**±4,6 %** et le 1 % low **±132,1 %**. Le score `Competitive` héritait du pire des deux (±72 %).

`tools/tailbench` mesure la **distribution nulle** : les mêmes captures découpées en fenêtres, un
protocole glissant, et un effet qui vaut **zéro par construction**. Tout écart trouvé est donc une
erreur, et le p95 de ces erreurs est la **résolution VRAIE** — pas celle qu'une formule annonce.

## Résolution vraie par grandeur (8 captures, protocole glissant)

| protocole | moyenne | p95 frametime | p99 frametime | 1 % low | 0,1 % low |
|---|---|---|---|---|---|
| 4 alternances de 15 s | 13,7 % | 11,1 % | 26,2 % | 47,4 % | 61,8 % |
| 8 alternances de 15 s | 5,2 % | 4,9 % | 15,0 % | 28,0 % | 47,1 % |
| 12 alternances de 15 s | 3,9 % | 3,8 % | 15,3 % | 15,7 % | 27,5 % |
| **24 alternances de 10 s** | **2,7 %** | **3,3 %** | 4,5 % | **8,7 %** | 21,1 % |
| 36 alternances de 10 s | 1,2 % | 2,0 % | 4,1 % | 5,6 % | 15,4 % |

**Le p95 frametime est une grandeur de queue qui résout comme la moyenne.** C'est lui qui rend
la fluidité décidable sans allonger les passes.

## À temps de jeu ÉGAL (240 s d'alternances)

| protocole | moyenne | p95 ft | 1 % low |
|---|---|---|---|
| **10 s × 24** | **2,7 %** | **3,3 %** | **8,7 %** |
| 15 s × 16 | 3,4 % | 3,2 % | 10,1 % |
| 20 s × 12 | 5,5 % | 7,2 % | 19,6 % |
| 30 s × 8 | 3,9 % | 4,6 % | 18,2 % |

La crainte « à 10 s le 1 % low ne porte que ~14 frames » était raisonnable et **fausse** : le
percentile d'une fenêtre est bien plus bruité, mais le modèle apparié en moyenne vingt-quatre au
lieu de seize, et le nombre de points gagne. `DureeParPasse` passe donc de 15 s à **10 s**.

## Méthodes essayées et REJETÉES

- **p99 frametime** — systématiquement **surconfiant** : il annonce 9,2 % quand son bruit vrai
  vaut 15,3 % (8 alternances), 6,7 % pour 10,0 % (12 alternances). Il fabriquerait des verdicts.
  Conservé comme grandeur informative, jamais décisionnelle.
- **Frames mises en commun par état** (au lieu d'agrégées par fenêtre) — **empire** le 1 % low :
  110 % puis 165 % de bruit vrai quand on ajoute des données. Le pire centile d'un grand pool ne
  sélectionne plus que quelques à-coups géants. Contre-intuitif, et mesuré.
- **0,1 % low comme grandeur de décision** — 21 à 707 % de bruit vrai selon le protocole, et sous
  un effet **réel de +2 %** il rend une médiane de **−3,2 %** : il se trompe de *sens* plus souvent
  qu'il ne voit juste. Sur une passe de 10 s à 140 fps il ne repose que sur ~2 frames.

## Le défaut principal n'était pas la grandeur, c'était le SEUIL

L'effet apparié était jugé contre la résolution d'une phase d'A/A à **trois** passes — t95(1) =
12,706, le pire estimateur de bruit possible. Le seuil annoncé ne descendait donc jamais, quel que
soit le nombre d'alternances ajoutées ensuite :

| protocole | annonçait | bruit vrai |
|---|---|---|
| 4 alternances | 15,7 % | 13,7 % |
| 8 alternances | 14,3 % | 5,2 % |
| 12 alternances | 14,3 % | 3,9 % |

Le produit était **sous-confiant d'un facteur ~3,7**. Il ne fabriquait pas de faux verdicts : il
était simplement aveugle à tout ce qui vaut moins de 14 %.

L'expérience appariée annonce désormais **sa propre** résolution — `var(c) = σ²·Stt/det`, avec σ²
le résidu du modèle et `df = n−3`. Recalibrée sur les mêmes captures :

| protocole | annonce | bruit vrai | faux positifs |
|---|---|---|---|
| 8 alternances de 15 s | 5,3 % | 5,2 % | 3,4 % |
| 12 alternances de 15 s | 4,6 % | 3,9 % | 0,0 % |
| 24 alternances de 10 s | 3,3 % | 2,7 % | 0,0 % |

## Le protocole final

```
3 fenêtres d'A/A          → la scène dérive-t-elle ? (sur la MOYENNE, pas sur le score)
puis A-B-B-A, B-A-A-B, …  → jusqu'à 24 alternances de 10 s
```

- A-B-B-A est **exactement orthogonal au temps** (r² = 0 contre 0,2 pour A-B-A-B) ; son miroir
  B-A-A-B aussi. On empile donc les blocs sans jamais recorréler l'état et l'horloge.
- **Arrêt séquentiel** après chaque bloc : une régression prouvée ou un gain de fluidité prouvé
  terminent l'expérience. Un « non résolu » ne l'arrête **jamais** — c'est ce qui empêche les
  regards répétés de gonfler le taux de faux positifs.
- Pire cas : 27 passes de 10 s = **4 min 30** de jeu.

## Ce qui reste à faire

- La détection d'un effet **+2 %** reste faible (≈ 10 % à 24 alternances) : la résolution annoncée
  (3,3 %) est encore au-dessus de l'effet cherché. L'objectif d'ingénierie — Avg sous ±2 % sur une
  scène Warzone convenable — demande ~36 alternances, soit 6 min de jeu. Non tranché.
- Les lignes à 36 et 48 alternances reposent sur peu de positions glissantes (21 et 9) : elles
  portent leur propre incertitude et ne sont pas des chiffres de référence.


---

# Troisième mesure — 08/09/2026 : tentative de DESTRUCTION des conclusions ci-dessus

Les deux mesures précédentes ont été menées avec des **fenêtres glissantes**. Elles restent
utiles, et elles ne prouvent pas ce que j'ai écrit qu'elles prouvaient.

## Ce qui était faux dans la section précédente

**« Résolution VRAIE »** — c'est une estimation *empirique sur un corpus* de huit parties.
Le mot est retiré de tout le produit.

**« 92 positions »** — deux positions glissantes décalées d'une fenêtre partagent 6 fenêtres
sur 7. Les compter comme 92 témoignages revient à demander deux fois son avis à la même personne.
L'unité indépendante est la **capture**.

**L'arrêt séquentiel** — j'avais écrit qu'« un `Unresolved` ne stoppe jamais, donc les regards
répétés ne gonflent pas les faux positifs ». C'est faux et c'est faux à l'envers : ne s'arrêter que
sur les succès est précisément ce qui crée le biais. Le protocole est redevenu **fixe**.

## Fenêtres NON chevauchantes, agrégées par capture (8 alternances de 10 s)

Corpus complet : 8 captures, 302 550 frames, **36,1 minutes de jeu**.

| grandeur | expériences disjointes | faux verdicts | écart max par capture | annonce médiane |
|---|---|---|---|---|
| moyenne | 15 | **0** | 1,5 % → 15,0 % | 3,8 % → 30,5 % |
| 1 % low | 13 | **0** | 0,4 % → 43,7 % | 9,6 % → 106,7 % |
| p95 frametime | 15 | **0** | 2,0 % → 11,3 % | 3,2 % → 17,7 % |

**La résolution n'est pas une constante du produit** : elle varie d'un facteur ~8 d'une partie
à l'autre. C'est l'expérience qui annonce la sienne, session par session.

## Leave-one-capture-out

La grandeur est **choisie sur 7 captures**, puis évaluée sur la 8ᵉ. Aucune frame du holdout ne sert
au choix.

```
8 holdouts · 15 expériences indépendantes · 0 faux verdict · 1 invalide
la procédure choisit le p95 frametime sur 7 holdouts sur 8
effet +2 % injecté retrouvé : 4/15
```

**Le choix est stable sur une partie jamais vue.** C'est le seul résultat de ce document qui
mérite d'être appelé une validation.

**La puissance reste faible** : 4/15 sur un vrai +2 %. La résolution annoncée (~5 %) dépasse
l'effet cherché.

## Le protocole de production n'est PAS validable sur ce corpus

À 24 alternances (270 s par expérience), le corpus ne laisse que **3 expériences disjointes sur
2 captures** — six holdouts sur huit n'en ont aucune. Ce qui est validé indépendamment, c'est
**8 alternances de 10 s**, pas 24.

Ce n'est pas un argument pour redescendre à 8 : plus de passes ne peut pas dégrader l'estimateur,
et la résolution annoncée décroît proprement avec les degrés de liberté. C'est un argument pour
**dire que 24 repose sur la théorie, pas sur une validation hors échantillon.**

## Contrôles synthétiques — un gain n'a pas toujours la forme d'un +2 % uniforme

| forme | moyenne | 1 % low | p95 ft | ce que la politique fait |
|---|---|---|---|---|
| A débit uniforme −2 % | +2,4 % | +1,6 % | +2,1 % | gain, sous-puissant |
| B queue seule améliorée | +2,4 % | +16,9 % | +6,4 % | discrimine correctement |
| **C moyenne +3 %, à-coups ajoutés** | −1,5 % | **−65,6 %, régression 13/14** | +1,6 % | **REFUS** attendu |
| D p95 amélioré, extrême intact | +2,7 % | +0,4 % | +6,7 %, gain 11/15 | discrimine correctement |
| E saut de phase à mi-expérience | +0,2 % | +0,0 % | −1,1 % | **aucune conclusion** attendu |

**La forme C justifie de garder le 1 % low.** Un réglage qui monte la moyenne en ajoutant des
à-coups rend **+1,6 % sur le p95** — un gain léger. Le p95 seul l'aurait laissé passer ; c'est le
1 % low qui prouve la régression et interdit le keep.

**Le p95 n'est donc pas « la fluidité ».** Il décrit les 5 % de frames les plus lentes, pas le pire
centile. Les deux grandeurs sont nécessaires, et le produit doit pouvoir dire « régularité p95
améliorée » ET « 1 % low non résolu » dans la même phrase.

Mon premier contrôle E était mal construit : j'appliquais ×0,70 à toutes les fenêtres de l'état
B et j'appelais ça un saut de phase. C'était un effet **uniforme** de +43 %, que le banc a trouvé et
déclaré gain — réponse correcte à la mauvaise question. Un saut de phase ne dépend pas de l'état :
la machine change de régime au milieu de l'expérience, A comme B.
