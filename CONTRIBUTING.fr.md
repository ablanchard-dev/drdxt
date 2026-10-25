# Contribuer à DrDXT

English version: [CONTRIBUTING.md](CONTRIBUTING.md).

DrDXT est gratuit et sous GPL v3. Trois façons d'aider, de la plus simple à la plus technique.

## 1. Prouver DrDXT sur ton matériel

C'est l'aide la plus utile : DrDXT est mis au point sur peu de machines, et rien ne se dit « validé » sans preuve.

- **Vérifier DrDXT sur mon PC** (dans l'App) pose chaque réglage, le relit, le rend et le relit encore, avec ton
  accord. Le rapport arrive dans `Documents`. Ouvre une issue « Rapport de vérification » et joins-le.
- **Sans installer DrDXT** : les zips de la [Release](https://github.com/ablanchard-dev/drdxt/releases) lisent ton PC,
  ou font la même vérification, et écrivent le même rapport dans `Téléchargements` (mode d'emploi : `LISEZ-MOI.txt` et
  `LISEZ-MOI-VALIDATION.txt`).
- **Exporter ma machine** écrit ce que DrDXT lit sur ton PC, sans rien qui te désigne
  ([docs/TESTEUR-EXPORTER-MA-MACHINE.md](docs/TESTEUR-EXPORTER-MA-MACHINE.md)). Ouvre une issue « Machine » et
  joins le fichier.

Un rapport validé entre dans `tests/Shared/validations` (nommé `<architecture>-<carte>-<date>.txt`), puis son
architecture s'ajoute à `GpuTuningCoverage.ValideesEnVie` ; la garde refuse l'inverse
([docs/COUVERTURE.md](docs/COUVERTURE.md) dit ce que DrDXT fait sur quel PC).

**Tu as un processeur Ryzen ou une carte Radeon ?** C'est ce qui manque le plus : rien de ce qui touche AMD n'a été
vérifié sur une vraie machine. [docs/VALIDATION-AMD.fr.md](docs/VALIDATION-AMD.fr.md) liste chaque chemin, comment le
tester et ce que le rapport doit montrer ; le rapport part par une issue « Validation AMD ».

## 2. Signaler un problème

Une issue « Bug » : ce que tu as fait, ce que tu attendais, ce qui s'est passé, et ta version de DrDXT (en bas
de l'écran). Joins le rapport de diagnostic (« Voir le rapport ») si l'App en a produit un. Une faille de sécurité
se signale en privé : voir [SECURITY.md](SECURITY.md). Une question n'est pas un bug : pose-la dans les
[Discussions](https://github.com/ablanchard-dev/drdxt/discussions) ou sur [Discord](https://discord.gg/z6HJWDzfB3).

## 3. Écrire du code

Commence par [ARCHITECTURE.md](ARCHITECTURE.md) (en anglais) : les projets, les règles que le code tient, et comment
ajouter un réglage de bout en bout. Un nouveau réglage passe aussi la liste de sûreté de
[docs/AJOUTER-UN-REGLAGE.fr.md](docs/AJOUTER-UN-REGLAGE.fr.md).

Prérequis : Windows 10 ou 11, le SDK .NET 10, et un clone dans un dossier court (par exemple `C:\src\drdxt`) : sous un
dossier profond, des fichiers de la sortie de build dépassent la longueur que Windows accepte pour charger une
bibliothèque native, et les tests de signature échouent sans rapport avec le code.

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

Les tests `Category=Machine` lisent le vrai matériel ; ils tournent à la main, pas en CI.

Pour voir l'App sans le matériel, un build Debug rejoue une machine enregistrée de `tests/Shared/machines` (une carte
AMD, un portable sur batterie, un compte sans droits administrateur…) ; ses données vont dans un dossier jetable :

```
dotnet build src\DrDxt.App -c Debug
src\DrDxt.App\bin\Debug\net10.0-windows\DrDxt.App.exe machine=amd-rx6600-ryzen5-5600 --langue=fr-FR
```

`tools/parcours-ui/parcours.ps1 -Machine <nom> -Langue en-US` fait le même rejeu hors écran, visite chaque page et
relève par UI Automation ce qu'un lecteur d'écran lirait : contrôles sans nom, clés non traduites, gabarits non
remplis, français resté dans l'interface anglaise. Lecture seule : il ne clique jamais.

**Un changement qui écrit sur la machine** (un réglage nouveau ou modifié, la liste blanche du helper, une
annulation, l'installeur ou le désinstalleur) n'est fusionné qu'après la relecture d'une deuxième personne, et avec la
CI verte.

**Les dépendances.** On s'appuie sur les alertes de sécurité (Dependabot alerts), sans les mises à jour de version :
rien n'ouvre de demande de fusion tout seul. Une montée de version se fait à la main dans le labo privé,
relue et testée comme tout changement, puis arrive dans ce dépôt avec la copie suivante.

Les règles que la suite fait respecter :

- **Un correctif commence par un test rouge**, puis le code le fait passer. Une garde qu'on ne peut pas faire
  tomber ne prouve rien : chaque garde a son témoin.
- **Les réglages de Windows et du matériel s'écrivent par le helper**, sous sa liste blanche ; les valeurs de la
  session de l'utilisateur s'écrivent avec son jeton, après la même liste. Chaque écriture a son inverse, lu avant
  d'écrire, et un test qui la rend. La table des sites d'écriture ([docs/NON-NUISANCE.md](docs/NON-NUISANCE.md)) est
  recalculée par la CI.
- **Aucun gain annoncé sans sa source** : un chiffre affiché porte une adresse, sinon il n'est pas affiché.
- **Ce qui quitte la machine est dit dans le README** ; un test vérifie les adresses écrites dans le code qui fait
  l'appel réseau.
- **Les textes de l'App existent en français et en anglais.**
- Le compte de tests du README suit la dernière exécution (`python tools/gates/compte_des_tests.py`).

Le code est écrit en grande partie en français, c'est voulu : identifiants, commentaires, noms de tests ; le nouveau
code suit la même règle. Chaque fichier s'ouvre sur un court résumé en français (`/// <summary>`, deux à quatre
lignes) : ce que fait la
pièce, pour qui, et pourquoi elle existe. Un commentaire dit pourquoi, git garde l'historique. Les tickets et les
demandes de fusion sont bienvenus en français ou en anglais. Un commit dit ce qui change pour le joueur, puis pourquoi.
