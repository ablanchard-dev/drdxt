# Testeur : exporter ta machine

DrDXT est mis au point sur une seule machine (Intel 14e génération, RTX 4060, bureau). Pour savoir ce qu'il
dit sur une carte AMD ou Intel Arc, un portable, un vieux PC ou un compte sans droits administrateur, il faut ce
que DrDXT lit sur ces machines-là. Le bouton « Exporter ma machine » écrit cette lecture dans un fichier, que tu
envoies toi-même si tu veux aider.

## Comment faire

1. Ouvre DrDXT et lance une analyse.
2. En bas de l'écran de résultat, sous « Voir le rapport », clique sur **Exporter ma machine**.
3. L'écran affiche le chemin du fichier : `Documents\machine-exportee-<date>.json`.
4. Ouvre-le dans le Bloc-notes si tu veux voir ce qu'il contient, puis envoie-le (Discord, courriel, comme tu
   préfères). DrDXT n'envoie rien : le fichier reste sur ton PC tant que tu ne l'envoies pas.

Pas besoin des droits administrateur : sans eux, le fichier dit justement ce qu'un compte sans droits voit, et
c'est utile aussi.

## Ce qu'il contient

- **Le matériel** : modèle du processeur et nombre de cœurs, carte(s) graphique(s), mémoire vidéo et version du
  pilote, barrettes de mémoire (référence, fabricant, fréquence, tension, emplacement), carte mère (fabricant,
  modèle), version et date du BIOS, type de machine (bureau, portable).
- **Les réglages lus** : profil mémoire XMP/EXPO, Resizable BAR, plan d'alimentation, HAGS, mode Jeu, cœurs
  parqués, VRR, fréquences des écrans, réglages des cartes réseau (nom et modèle de la carte), dates des arrêts
  brutaux récents, etc.
- **Les constats du docteur**, tels que l'écran les montre : services qui échouent, tâches planifiées et services
  dont le fichier peut être remplacé (avec leur chemin), incidents noyau, logiciels et pilotes installés ou mis à
  jour récemment, etc.
- Si DrDXT tournait avec les droits administrateur, et la version de DrDXT.

## Ce qui en est retiré

Avant d'écrire le fichier, DrDXT retire de chaque texte :

- le dossier de n'importe quel compte (`C:\Users\<nom>`) : il devient `C:\Users\joueur`, la suite du chemin reste ;
- le nom de ton compte Windows (même de deux lettres), le nom du PC et son domaine ;
- les numéros de série et identifiants matériels lus sur ce PC (BIOS, carte mère, disques, mémoire, UUID de la
  machine, numéros de volume) ;
- les adresses MAC ;
- les adresses IP (v4 et v6) ;
- les identifiants de compte Windows (SID) ;
- les identifiants de volume (`Volume{…}`) ;
- les adresses de courriel ;
- le nom d'organisation des dossiers OneDrive (`OneDrive - <organisation>`).

Chaque valeur retirée devient `<retiré>`. Cette liste est celle du code (`MachineExportee.CeQuiEstRetire`), et un
test vérifie qu'aucune de ces valeurs, semée dans une fausse machine, ne ressort.

## Ce que le retrait ne couvre pas

- **Les noms que tu as choisis toi-même** : une tâche planifiée, un dossier hors de ton profil, une carte réseau
  renommée. S'ils contiennent ton nom de compte, il est retiré ; s'ils contiennent autre chose (un pseudo, un
  prénom différent du compte), ça reste. Relis le fichier avant de l'envoyer si ça t'inquiète.
- **Un nom de compte d'une seule lettre** n'est pas retiré des textes (il le serait partout) ; seul ton dossier
  `C:\Users\<nom>` devient `joueur`.
- **La liste des logiciels et pilotes récents** que le docteur mentionne reste : elle dit ce qui est installé sur
  ta machine.
- Un numéro de version à quatre nombres (`1.4.6.3`) a la forme d'une adresse IP : dans les textes du docteur, il
  est retiré lui aussi. Les versions du matériel (pilote, BIOS) restent.

## Pour le mainteneur

Le fichier reçu se pose tel quel dans `tests/Shared/machines/` (renommé si besoin : le nom du fichier sert de nom
de machine) ; les tests `MachinesEnregistreesTests` le rejouent jusqu'à l'écran, et en DEBUG
`DrDxt.App.exe machine=<nom>` l'affiche dans l'App. Le même format sort de `tools/enregistreur`.
