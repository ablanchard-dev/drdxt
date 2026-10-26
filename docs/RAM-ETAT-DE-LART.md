# La memoire : ce qui est etabli, ce qui ne l'est pas

Ce document est la source des affirmations que DrDXT a le droit de faire sur la memoire vive.
Il est ecrit pour etre lu par un test : `TheRamStateOfTheArtHasNoUnsourcedClaimTests`.

**La regle du fichier, et elle est mecanique** : toute ligne du tableau AFFIRMATIONS porte soit une
source (une URL) ET une date de consultation, soit la fourchette exacte `UNKNOWN`. Une affirmation
chiffree sans source ne compile pas le test. Il n'existe pas de troisieme cas.

Classes d'affirmation, identiques a celles de l'ecran de scan :

- `MESURE ICI` — un delta releve sur la machine du client par notre propre instrument. **Aucune ligne
  de ce document n'y a droit** : ce fichier ne contient que du savoir externe.
- `SOURCE` — mesure faite ailleurs, sur une autre machine. S'affiche toujours avec la mention
  << mesure ailleurs, pas sur ta machine >>.
- `NON CHIFFRE` — le phenomene est etabli, son ampleur ne l'est pas. Ces lignes n'ont aucun nombre.

Qualite de la source, notee parce qu'elle change ce qu'on a le droit d'en tirer :

- `PRIMAIRE` — l'organisme de normalisation ou le constructeur (JEDEC, Intel, AMD, Microsoft).
- `SECONDAIRE` — un banc de test independant, date, avec son materiel decrit.
- `TERTIAIRE` — un forum, un article de synthese. Suffisant pour dire *que* le phenomene existe,
  jamais pour en tirer un nombre affiche au client.

## AFFIRMATIONS

| id | affirmation | fourchette | classe | qualite | source | consultee le |
|---|---|---|---|---|---|---|
| R-01 | Faire tourner la memoire sur un seul canal (une barrette, ou deux barrettes du meme cote) coute des FPS, et coute surtout des 1 % low | 5 a 34 % selon le titre ; 1 % low les plus touches, 20 a 33 % | SOURCE | SECONDAIRE | https://www.noobfeed.com/articles/single-vs-dual-ddr5-gaming-performance (Ryzen 7 9700X, G.Skill DDR5-6000 CL30, 16 Go 1x contre 32 Go 2x) | 2026-09-06 |
| R-02 | L'ecart mono/dual canal se resserre quand le GPU redevient le facteur limitant (haute resolution, reglages eleves) | 4 % de 1 % low en 4K sur Arc Raiders, contre 27 % en 1080p, meme banc | SOURCE | SECONDAIRE | https://www.noobfeed.com/articles/single-vs-dual-ddr5-gaming-performance | 2026-09-06 |
| R-03 | DDR5-4800 est la vitesse JEDEC native de reference ; 5600, 6000 et 6400 annonces sur la boite sont le plus souvent un profil XMP ou EXPO pose par-dessus le SPD | UNKNOWN | NON CHIFFRE | PRIMAIRE | https://www.jedec.org/news/pressreleases/jedec-publishes-update-ddr5-sdram-standard-used-high-performance-computing (JESD79-5A porte les timings jusqu'a 6400 MT/s) | 2026-09-06 |
| R-04 | Le dual rank abaisse le plafond de frequence stable du controleur memoire, et exige des timings plus laches | UNKNOWN | NON CHIFFRE | TERTIAIRE | https://www.faceofit.com/ddr5-single-vs-dual-rank-2-vs-4-sticks/ | 2026-09-06 |
| R-05 | Peupler les quatre emplacements d'une carte grand public (topologie daisy chain) abaisse la frequence stable atteignable, par reflexion de signal | UNKNOWN | NON CHIFFRE | TERTIAIRE | https://www.faceofit.com/ddr5-2-vs-4-sticks/ | 2026-09-06 |
| R-06 | La compression memoire de Windows reduit les acces disque en situation de memoire contrainte ; la desactiver degrade le plus souvent au lieu d'ameliorer | UNKNOWN | NON CHIFFRE | TERTIAIRE | https://www.ninjaone.com/blog/enable-or-disable-memory-compression-in-windows-11/ | 2026-09-06 |

## MYTHES — nommes ici pour que personne ne les reimplemente

Un mythe est une manipulation memoire populaire dont le gain n'est demontre nulle part, et dont le
cout est reel. Le produit ne les propose pas. Il a le droit de dire au client qu'il ne les fera pas.

| id | mythe | ce que le produit fait a la place | ou c'est ecrit dans le code -- ou l'API dont l'ABSENCE le prouve |
|---|---|---|---|
| M-01 | Couper le fichier d'echange fait gagner des FPS | Diagnostique le pagefile coupe et propose de RETABLIR la gestion automatique | `src/DrDxt.Application/Tweaks/FixIt/SanePagefileTweak.cs` |
| M-02 | Un << RAM cleaner >> qui vide la memoire de travail accelere le jeu | Rien, et c'est une absence PROUVEE : vider un working set force le systeme a tout relire ensuite | aucun code -- `EmptyWorkingSet` et `SetProcessWorkingSetSize` sont absents de src/ |
| M-03 | Vider la standby list periodiquement supprime les micro-saccades | Rien, absence prouvee. La standby list est du cache ; la vider jette un cache chaud | aucun code -- `SetSystemFileCacheSize` et `MemoryPurgeStandbyList` sont absents de src/ |
| M-04 | Desactiver SysMain rend la machine plus rapide | **Le produit le DETECTE et le signale comme une regression** deja infligee par un << optimiseur >> precedent. Il ne penalise jamais pour ca | `Finding.Memory.SysMainOff` |
| M-05 | Mettre `SystemResponsiveness` a 0 libere le CPU pour le jeu | Detecte comme une regression, meme doctrine que M-04. C'est le mythe le plus recopie du web | `GetMmcssIntact` |

## CE QUE LE PRODUIT NE SAIT PAS DE TA MEMOIRE

Liste destinee a l'ecran, pas au tiroir. Chaque ligne est une chose que le client pourrait croire
que nous savons et que nous ne savons pas.

- Les timings reels (CL, tRCD, tRP, tRAS) : `Win32_PhysicalMemory` ne les rend pas.
- ~~Le debit et la latence memoire reels~~ : **MESURES depuis le 06/09/2026** par `MemoryBench`,
  avec un self-check qui refuse une mesure invalide (`MemoryQuality`).
- Le rapport UCLK:MCLK, donc si le controleur tourne en 1:1 ou en 1:2.
- Si le kit installe est stable : nous lisons une frequence configuree, nous ne testons rien.
- Le nombre de canaux annonce par le CPU. Nous le deduisons des emplacements (voir R-01), et depuis
  le 06/09 la mesure peut **prouver un minimum** : un debit au-dela du plafond theorique d'un canal
  (MT/s x 8 octets) prouve le deuxieme. **Le sens inverse reste inconnu** : un debit bas ne
  prouve pas le simple canal, un seul fil d'execution ne saturant pas forcement deux canaux.
- Les tailles de cache, malgre l'escalier : un saut de latence n'est pas une frontiere de cache.
  Mesure du 06/09 : **5 sauts pour 3 frontieres**, et aucun seuil ne les separe.
- Ce que le client a regle lui-meme dans son BIOS.

## Ce que ce document ne dit pas

Il ne dit pas ce que DrDXT gagne sur la machine du client. Le plancher de bruit mesure sur le banc
du 01/09 est de 9,9 % ; toute affirmation `MESURE ICI` doit passer cet instrument, et aucune ligne
ci-dessus n'y pretend. Voir `GATES.md`.
