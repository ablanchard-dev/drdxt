# Deux trous trouves par l'audit du 07/09, et ce que les sources en disent

Ce document ouvre les deux angles morts que l'audit du scanner a montres sur la machine de
reference. Il est ecrit pour etre lu par un test : `TheIdleAndNetworkStateOfTheArtTests`.

**La regle du fichier, mecanique** : toute ligne porte soit une source (URL) ET une date de
consultation, soit la fourchette exacte `UNKNOWN`. Un chiffre sans source ne compile pas le test.

## TROU 1 — LA CONSOMMATION AU REPOS, PLUSIEURS ECRANS

Constat de depart, mesure ici : la carte de reference tire **55,9 W au repos** (relevee deux fois a
quatre heures d'intervalle, 55,2 puis 55,9 W) alors qu'elle plafonne a **115 W en charge**. La
machine porte **deux ecrans a des frequences DIFFERENTES : 200 Hz et 144 Hz**.

| id | affirmation | fourchette | classe | qualite | source | consultee le |
|---|---|---|---|---|---|---|
| R-01 | Deux ecrans a des frequences differentes empechent la memoire video de descendre dans son etat de repos : l'horloge memoire reste au palier haut et la consommation avec | UNKNOWN | NON CHIFFRE | TERTIAIRE | https://forums.developer.nvidia.com/t/high-idle-consumption-multiple-screen-setup/302946 | 2026-09-07 |
| R-02 | Baisser la frequence d'un ecran fait retomber la consommation au repos | de 30-40 W a 21,5 W dans un cas rapporte ; jusqu'a 95 W vers 35 W dans un cas extreme, l'horloge memoire passant de 9500 a 400 MHz | SOURCE | TERTIAIRE | https://www.xda-developers.com/i-fixed-my-gpus-ridiculous-40w-idle-power-draw-with-one-hidden-setting/ | 2026-09-07 |
| R-03 | Un support NVIDIA (niveau 2, signe « Josh NVCC ») a repondu « by design for 3 displays at a high refresh rate » sur une GTX 1080 et conseille de descendre sous 144 Hz hors jeu. Revu le 16/09/2026 : la page NVIDIA ne se charge plus sans JavaScript, la citation est relayee par un fil Tom's Hardware ; elle porte sur TROIS ecrans et une ancienne carte, pas sur deux ecrans d'une carte recente | UNKNOWN | NON CHIFFRE | SECONDAIRE | https://www.nvidia.com/en-us/geforce/forums/discover/233230/very-high-idle-clocks-on-gtx-1080-with-multiple-displays/ | 2026-09-07 |

**Pourquoi ce levier merite d'exister alors qu'il ne rend aucun FPS** : il ne coute rien a
tester, il est reversible en trois clics, et il agit **24 h sur 24** au lieu des heures de jeu.
Sur une annee, c'est le seul levier de ce depot dont l'effet ne depend pas du temps passe a jouer.

**Et ce qu'il ne faut pas en faire** : baisser la frequence de l'ecran PRINCIPAL couterait des
images en jeu -- exactement le defaut que `Finding.Display.RefreshBelowMax` denonce. Le conseil ne
peut viser que le SECOND ecran, et seulement hors jeu.

## TROU 2 — LES ECONOMIES D'ENERGIE DE LA CARTE RESEAU

Constat de depart, mesure ici : la carte Realtek 2.5GbE de la machine de reference porte
**`Ethernet vert` = Active** et **`Power Saving Mode` = Active**. `NetworkAdvisor` ne lit ni l'un ni
l'autre : il ne couvre que la moderation d'interruption, RSS et EEE (qui, lui, est desactive).

| id | affirmation | fourchette | classe | qualite | source | consultee le |
|---|---|---|---|---|---|---|
| N-01 | L'Ethernet econome en energie (802.3az) endort le lien quand il est inactif et le reveille a la demande ; le reveil se compte en microsecondes (reveil PHY maximal d'environ 16 us en 1000BASE-T selon le groupe de travail IEEE 802.3az, https://www.ieee802.org/3/az/public/jul08/grimwood_02_0708.pdf, recoupe le 2026-09-17) | UNKNOWN | NON CHIFFRE | SECONDAIRE | https://arxiv.org/pdf/1707.02798 | 2026-09-07 |
| N-02 | Le surcout de delai induit est borne et, dans la plupart des scenarios, negligeable -- l'exception etant les usages les plus sensibles au delai | UNKNOWN | NON CHIFFRE | SECONDAIRE | https://arxiv.org/pdf/1707.02798 | 2026-09-07 |
| N-03 | Pour le jeu, les guides conseillent de desactiver ces economies pour la CONSTANCE, pas pour la vitesse moyenne | UNKNOWN | NON CHIFFRE | TERTIAIRE | https://helpzone.blog/energy-efficient-ethernet-should-you-enable-or-disable-it/ | 2026-09-07 |

**aucun CHIFFRE N'EXISTE POUR LE JEU.** Les trois lignes sont `NON CHIFFRE`, et c'est le
resultat de la recherche, pas une prudence de facade : le phenomene est etabli (il y a bien un
reveil), son cout en jeu n'est mesure nulle part. Le produit a donc le droit de **signaler** ces
reglages, jamais de promettre ce que les couper rapporte.

Et une limite d'instrument, qui decide de la suite : **notre plancher de bruit en jeu est de
9,9 %**. Un effet qui se compte en microsecondes par reveil est plusieurs ordres de grandeur en
dessous. Nous ne pourrons **jamais** le mesurer avec ce que nous avons -- donc nous ne le
promettrons jamais.

## CE QUE CES DEUX TROUS ONT EN COMMUN

Aucun des deux ne rend des FPS. Le premier rend des **watts et du silence**, en continu ; le second
rend de la **constance**, et personne ne sait combien. Les mettre dans le produit ne change donc pas
ce qu'il promet -- ca change ce qu'il SAIT VOIR, et c'est la difference entre un scanner qui dit
« rien a signaler » et un scanner qui a vraiment regarde.
