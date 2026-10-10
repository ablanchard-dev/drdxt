# Confidentialité

English version: [PRIVACY.md](PRIVACY.md).

DrDXT n'a ni compte, ni télémétrie, ni serveur à lui. Ce qu'il lit sur ton PC y reste, sauf les deux fichiers que tu
choisis d'envoyer toi-même, plus bas.

## Ce que DrDXT lit, et où il le garde

Pour te dire ce qui freine ton PC, DrDXT lit le matériel (processeur, carte graphique, mémoire, disques, écran), les
réglages de Windows et des pilotes qui comptent en jeu, les journaux d'événements de Windows (arrêts brutaux, erreurs
matérielles, mises à jour) et, pendant une partie, le rythme des images de ton jeu (pas leur contenu) et le ping vers son serveur.

Ce qu'il en garde reste sur ta machine :

- `%LOCALAPPDATA%\DrDXT` : ton historique (scans, parties mesurées, réglages posés et leur valeur d'origine) ;
- `%ProgramData%\DrDxt` : ce que DrDXT a posé pour pouvoir le rendre, et le journal du programme qui écrit les réglages ;
- `Documents` : seulement les fichiers que tu demandes (rapport, export de ta machine).

La désinstallation retire le programme, son service et ce qu'il a posé ; ton historique reste dans
`%LOCALAPPDATA%\DrDXT` (le détail exact est dans le README). Supprimer ce dossier et `%ProgramData%\DrDxt` efface tout.

## Ce qui part sur le réseau

Seulement ce que liste la section « Ce qui quitte la machine » du README : la version publique des pilotes graphiques,
des pings, la vérification de mise à jour si elle est configurée, et les pages que tu ouvres toi-même. Rien qui te
désigne. Un test échoue si le code contacte une adresse que le README ne nomme pas.

## Les deux fichiers que tu peux envoyer

**« Envoyer mon rapport »** ouvre ton navigateur sur un ticket GitHub déjà rempli : le titre porte le modèle de ton
processeur et de ta carte graphique, le corps le rapport de « Vérifier DrDXT sur mon PC » (les réglages essayés, leur
valeur d'origine, ce qui a été relu). DrDXT n'envoie rien lui-même. Tu relis le ticket avant de le soumettre ; une fois
soumis, il est lisible par ceux qui voient le dépôt, sous ton compte GitHub.

**« Exporter ma machine »** écrit un fichier dans tes Documents, que tu envoies si tu veux que ta machine serve de test.
Avant l'écriture, DrDXT en retire :

- le dossier de n'importe quel compte (X:\Users\<nom>) : il devient X:\Users\joueur, la suite du chemin reste
- le nom du compte Windows (même de deux lettres), le nom du PC et son domaine
- les numéros de série et identifiants matériels lus sur ce PC (BIOS, carte mère, disques, mémoire, UUID de la machine, numéros de volume)
- les adresses MAC (avec tirets, deux-points ou collées, pour celles des cartes de ce PC)
- les adresses IP (v4 et v6) ; une version à quatre nombres (1.4.6.3) a la forme d'une adresse v4 et part aussi, sauf dans les champs de version (DriverVersion)
- les identifiants de compte Windows (SID S-1-5-21-…)
- les identifiants de volume (Volume{…})
- les adresses de courriel
- le nom d'organisation des dossiers OneDrive (« OneDrive - <organisation> »)

Ce qui reste : les modèles de ton matériel, tes réglages, les noms de tes périphériques (souris, casques) et des
programmes qui démarrent avec Windows. Relis le fichier avant de l'envoyer si l'un d'eux te gêne.

## Une question

Par un ticket sur le dépôt, ou en privé comme le dit `SECURITY.md`.
