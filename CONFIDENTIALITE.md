# Confidentialité

English version: [PRIVACY.md](PRIVACY.md).

DrDXT n'a ni compte, ni télémétrie, ni serveur à lui. Ce qu'il lit sur ton PC y reste, sauf les fichiers que tu
choisis d'envoyer toi-même, plus bas.

## Ce que DrDXT lit, et où il le garde

Pour te dire ce qui freine ton PC, DrDXT lit le matériel (processeur, carte graphique, mémoire, disques, écran), les
réglages de Windows et des pilotes qui comptent en jeu, les journaux d'événements de Windows (arrêts brutaux, erreurs
matérielles, mises à jour), les rapports que ton jeu écrit en plantant, les réglages et les journaux d'OBS (encodeur,
débit, images perdues ; jamais tes scènes, ton compte ni tes chemins), le volume que chaque programme envoie pendant
3 s du scan (son nom et un débit, jamais le contenu ni l'adresse), les rapports que ta souris et ta manette envoient
quand tu lances leur mesure et, pendant une partie, le rythme des images de ton jeu (pas leur contenu) et le ping vers
son serveur.

Ce qu'il en garde reste sur ta machine :

- `%LOCALAPPDATA%\DrDXT` : ton historique (scans, parties mesurées, réglages posés et leur valeur d'origine) ;
- `%ProgramData%\DrDxt` : ce que DrDXT a posé pour pouvoir le rendre, le journal du programme qui écrit les réglages, et
  les témoins d'incident (l'heure d'un arrêt brutal et le nom des événements de Windows qui l'entourent, jamais leur
  message) ;
- `Documents` : seulement les fichiers que tu demandes (rapport, export de ta machine).

La désinstallation retire le programme et ses services, et rend les réglages qu'elle connaît ; ton historique reste
dans `%LOCALAPPDATA%\DrDXT`, et le reste est listé dans le README (« Ce qui reste »). Supprimer ce dossier et `%ProgramData%\DrDxt` efface tout.

## Ce qui part sur le réseau

Seulement ce que liste la section « Ce qui quitte la machine » du README : la version publique des pilotes graphiques,
des pings, une question à ta box sur le réseau de la maison (son adresse WAN, jamais affichée), la vérification de mise à
jour au démarrage (coupée par la variable `DRDXT_UPDATE_URL` à `off`), le test de ta ligne sous charge si tu le lances
(`speed.cloudflare.com`), la réparation de Windows si tu la lances (DISM télécharge depuis Windows Update), et les pages
que tu ouvres toi-même. Rien qui te
désigne. Un test échoue si le code contacte une adresse que le README ne nomme pas.

Les identifiants de ton compte Windows ne vont à un proxy que si quelqu'un l'a configuré : une variable HTTP(S)_PROXY, ou
un proxy manuel quand ni la détection automatique ni un script de configuration ne sont actifs. Un proxy trouvé tout seul
(WPAD) ou donné par un script ne reçoit rien, même s'il les demande : sur un réseau public, un faux proxy recevrait sinon
l'échange de connexion de ton compte. Derrière un proxy d'entreprise trouvé de cette façon et qui exige une connexion, la
vérification de mise à jour reste injoignable.

## Les fichiers que tu peux envoyer

**« Envoyer mon rapport »** ouvre ton navigateur sur le modèle de ticket GitHub, vide : rien de ta machine n'est dans
le lien. Tu y joins toi-même le rapport de « Vérifier DrDXT sur mon PC » (les réglages essayés, leur valeur d'origine, ce
qui a été relu), rangé dans tes `Documents`. DrDXT n'envoie rien lui-même. Tu relis le ticket avant de le soumettre ;
une fois soumis, il est lisible par ceux qui voient le dépôt, sous ton compte GitHub.

**« Préparer un paquet d'aide »** montre d'abord la liste des fichiers qu'il contiendra et le texte exact de chacun, déjà anonymisé, puis écrit un zip là où tu le choisis, que tu envoies toi-même (Discord, ticket). Il ne contient que le rapport du docteur, tes parties relevées, le journal des changements et le journal de plantage de DrDXT, passés par les mêmes retraits que l'export ci-dessous. Aucun autre fichier n'y entre, et jamais de variable ou de table du firmware.

**« Exporter ma machine »** écrit un fichier dans tes Documents, que tu envoies si tu veux que ta machine serve de test.
Avant l'écriture, DrDXT en retire :

- les dossiers connus du compte lus par Windows (Documents, Bureau, AppData, AppData local, et le profil hors de X:\Users), où qu'ils soient, même redirigés : ils deviennent un repère (<Documents>, <Desktop>…), la suite du chemin reste
- le dossier de n'importe quel compte (X:\Users\<nom>) : il devient X:\Users\joueur, la suite du chemin reste
- le nom du compte Windows (même de deux lettres), le nom du PC et son domaine
- les numéros de série et identifiants matériels lus sur ce PC (BIOS, carte mère, disques, mémoire, UUID de la machine, numéros de volume)
- la fin des identifiants d'appareil (USB\VID_…&PID_…\<exemplaire>) : un numéro de série, une adresse Bluetooth ; le modèle reste
- les adresses MAC (avec tirets, deux-points ou collées, pour celles des cartes de ce PC)
- les adresses IP (v4 et v6) ; une version à quatre nombres (1.4.6.3) a la forme d'une adresse v4 et part aussi, sauf dans les champs de version (DriverVersion)
- les identifiants de compte Windows (SID S-1-5-21-…)
- les identifiants de volume (Volume{…})
- les adresses de courriel
- le nom d'organisation des dossiers OneDrive (« OneDrive - <organisation> »)
- les clés à tirets (licence : au moins quatre groupes de cinq lettres ou chiffres) et les empreintes (32 caractères hexadécimaux ou plus)

Ce qui reste : les modèles de ton matériel, tes réglages, les noms de tes périphériques (souris, casques) et des
programmes qui démarrent avec Windows. Relis le fichier avant de l'envoyer si l'un d'eux te gêne.

## Une question

Par un ticket sur le dépôt, ou en privé comme le dit `SECURITY.md`.
