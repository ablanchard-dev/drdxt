# Les instruments, et ce qu'ils peuvent prouver

Un produit qui promet un gain doit posseder l'instrument capable de dire NON. Ce fichier est la
liste de ces instruments, avec **le plancher de bruit que chacun a rendu quand on l'a lance**.

**La regle, et c'est elle qui empeche la boucle de regresser** : un ecart plus petit que le
`sigma/moyenne` de l'instrument **n'est pas un resultat**. Une revue re-mesure chaque ligne ; un
plancher qui empire est un echec, pas une note de bas de page. Une ligne dont la mesure a plus de
7 jours est **PERIMEE** et ne peut servir a rien.

## REGISTRE

| id | instrument | ce qu'il mesure | plancher (sigma/moyenne) | n | mesure le | verdict |
|---|---|---|---|---|---|---|
| I-01 | `MemoryBench` (debit), lance par `tools/memprobe` | debit de lecture DRAM, buffer 256 Mo | 3,34 % | 10 | 2026-09-06 | UTILISABLE |
| I-02 | `MemoryBench` (latence), lance par `tools/memprobe` | latence DRAM, chaine de pointeurs 128 Mo | 4,53 % | 10 | 2026-09-06 | UTILISABLE |
| I-06 | `MemoryQuality` (self-check) | les quatre refus Q-01..Q-04 poses sur une mesure memoire | UNKNOWN | 2 | 2026-09-06 | UTILISABLE. Un verdict n'a pas de plancher de bruit : ce qui se mesure ici est qu'il sait dire NON *et* OUI, prouve dans les deux sens (P-06) |
| I-03 | `tools/r2bench` | debit de calcul GPU deterministe, checksum verifie | 0,122 % | 10 | 2026-09-06 | UTILISABLE |
| I-04 | `tools/r2bench` sous charge GPU concurrente | le meme, machine NON au repos | 0,837 % | 6 | 2026-09-06 | UTILISABLE mais NON COMPARABLE (voir P-01) |
| I-05 | rejeu in-game (`tools/smoke`) | delta de FPS entre deux series capturees | 6,46 % | 28 | 2026-09-06 | UTILISABLE au-dela de 13 % (voir P-04) |

## PROTOCOLES -- ce sans quoi le nombre ne veut rien dire

**P-01 — La machine au repos n'est pas une politesse, c'est une condition.** Mesure du 06/09 :
`r2bench` rend `8,076e11` au repos et `6,144e11` pendant qu'une autre charge GPU tourne, soit
**−23,9 % de niveau**. La variance, elle, reste petite dans les deux cas (0,12 % et 0,84 %).
⇒ L'instrument est **precis mais pas robuste** : il ne se trompe pas, il repond a une autre
question. Un A/B dont les deux moities n'ont pas la meme charge de fond compare deux machines
differentes. **C'est le mode de panne le plus dangereux de tout ce fichier** : il ne produit aucun
signe d'erreur, juste un « gain » de 24 % qui n'existe pas.

**P-06 — Un instrument qui n'a jamais dit NON n'a pas ete teste.** Mesure du 06/09 : `MemoryBench`
a ete lance deux fois de suite sur la meme machine. Sur une machine occupee (une compilation en
fond), il a **rejete sa propre mesure** en Q-03 -- sigma 7,35 % et 7,60 %, au-dela du seuil de 5 %.
Machine calmee, la mesure suivante est **ACCEPTEE** : 34,20 Go/s, 71,33 ns, 2 canaux prouves.
Et la mutation qui compte : en ramenant la chaine de latence a **16 Mo** (le bug reellement paye le
06/09, une chaine qui tient dans un L3 de 33 Mo), la sonde rend **20,23 ns** et le self-check
**REJETTE en Q-04**. Les deux sens sont donc prouves sur la machine, pas seulement sur des donnees
fabriquees. Le meme jour, six mutations du code ont chacune fait **echouer** le test qui les nomme.

**P-02 — La chauffe ne se compte pas.** La premiere passe paie l'allocation et les defauts de page.
`memprobe` en jette une avant de mesurer.

**P-03 — Meme graine, meme chemin.** La chaine de pointeurs de la latence est melangee avec une
graine fixe : deux executions parcourent le meme chemin, sinon l'A/B compare deux permutations en
croyant comparer deux reglages.

**P-04 — Le rejeu in-game ne se juge pas sur ce qu'il refuse.** Mesure du 06/09 : les **8 captures
du 01/09 sont toutes du meme etat**, aucun reglage n'a change entre elles. Comparees deux a deux
(**28 paires**), l'instrument voit des ecarts allant de **−14,0 a +21,5 fps** sur une base de
137,2 fps -- c'est-a-dire **le bruit d'une machine qui n'a pas bouge** : sigma/base **6,46 %**,
etendue **25,9 %**.

**Et les 28 verdicts disent `NoMeasurableGain`. Zero faux positif sur 28 essais.**
⇒ Cet instrument n'est pas hors service : il est **honnete et conservateur**. Il a refuse 28 fois
d'affirmer un gain la ou il n'y en avait aucun, y compris quand le hasard lui montrait +21,5 fps.
Sa limite est la **resolution**, pas la justesse : il faut un gain **au-dela d'environ 13 %**
(deux sigma) pour qu'il puisse le voir. Dire « il ne certifie pas +10 % » decrit un instrument qui
a raison, pas un instrument casse.

## RE-MESURES — la revue relance, elle ne recopie pas

Le plancher inscrit dans le registre est le **pire observe**, pas le dernier : un instrument se
specifie par ce sur quoi on peut compter.

| date | I-01 debit | I-02 latence | I-03 r2bench | verdict |
|---|---|---|---|---|
| 2026-09-06 (1re) | 1,86 % | 4,79 % | 0,122 % | mesures d'origine |
| 2026-09-06 (revue) | 1,84 % | 4,58 % | **0,094 %** | **aucun ne regresse, les trois s'ameliorent** |

## CE QUE CE FICHIER CORRIGE, ET IL FAUT LE DIRE

**`GATES.md` a porte pendant un jour la phrase « le banc deterministe varie de 15 a 73 %, il
n'est pas citable en l'etat ».** Mesure du 06/09, dix passes lancees pour l'occasion :
**sigma/moyenne = 0,122 %, etendue 0,43 %**. C'est le nombre le plus stable de tout le projet,
meilleur que la sonde memoire.

Le chiffre de 72,6 % du 05/09 **n'a pas ete reproduit**. Une charge GPU concurrente, testee
exprès, ne le reproduit pas non plus : elle decale le niveau de 24 % sans faire exploser la
variance. ⇒ Ce 72,6 % reste **UNKNOWN quant a sa cause**, et il ne doit plus etre cite comme un
fait sur l'instrument.

La lecon vaut plus que le chiffre : **une affirmation heritee d'un journal precedent n'est pas une
mesure.** Elle a ete recopiee deux fois avant que quelqu'un ne relance la commande, et relancer la
commande a coute deux minutes.

## Le compte, recalcule et non recopie

**6 instruments, 6 UTILISABLES**, dont un sous condition de protocole (I-04) et un sous condition
de resolution (I-05). **0 HORS SERVICE.** Les deux qui portaient ce verdict le portaient par
heritage, et aucun des deux ne l'a merite une fois relance.

**Corrige le 07/09/2026, et c'est le defaut que ce fichier denonce ailleurs.** Cette
phrase disait « 5 instruments, 4 UTILISABLES » alors que le registre en liste six, tous utilisables :
I-06 a ete ajoute le 06/09 sans que le total soit refait. Un chiffre recopie a la main pourrit des
l'entree suivante -- c'est pourquoi un test le RECALCULE desormais depuis le tableau
(`InstrumentsRegisterCountsItselfTests`) au lieu de faire confiance a cette ligne.
