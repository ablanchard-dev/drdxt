# Contribuer à DrDXT

English version: [CONTRIBUTING.en.md](CONTRIBUTING.en.md).

DrDXT est gratuit et sous GPL v3. Trois façons d'aider, de la plus simple à la plus technique.

## 1. Prouver DrDXT sur ton matériel

C'est l'aide la plus utile : DrDXT est mis au point sur peu de machines, et rien ne se dit « validé » sans preuve.

- **Vérifier DrDXT sur mon PC** (dans l'App) pose chaque réglage, le relit, le rend et le relit encore, avec ton
  accord. Le rapport arrive dans `Documents`. Ouvre une issue « Rapport de vérification » et joins-le.
- **Sans installer DrDXT** : les zips de la [Release](https://github.com/ablanchard-dev/drdxt/releases) lisent ton PC,
  ou font la même vérification, et écrivent le même rapport dans `Téléchargements` (mode d'emploi : `LISEZ-MOI.txt`).
- **Exporter ma machine** écrit ce que DrDXT lit sur ton PC, sans rien qui te désigne
  ([docs/TESTEUR-EXPORTER-MA-MACHINE.md](docs/TESTEUR-EXPORTER-MA-MACHINE.md)). Ouvre une issue « Machine » et
  joins le fichier.

Un rapport validé entre dans `tests/Shared/validations` (nommé `<architecture>-<carte>-<date>.txt`), puis son
architecture s'ajoute à `GpuTuningCoverage.ValideesEnVie` ; la garde refuse l'inverse
([docs/COUVERTURE.md](docs/COUVERTURE.md) dit ce que DrDXT fait sur quel PC).

## 2. Signaler un problème

Une issue « Bug » : ce que tu as fait, ce que tu attendais, ce qui s'est passé, et ta version de DrDXT (en bas
de l'écran). Joins le rapport de diagnostic (« Voir le rapport ») si l'App en a produit un. Une faille de sécurité
se signale en privé : voir [SECURITY.md](SECURITY.md).

## 3. Écrire du code

Prérequis : Windows 10 ou 11, le SDK .NET 10.

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

Les tests `Category=Machine` lisent le vrai matériel ; ils tournent à la main, pas en CI.

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

Les commentaires et les textes sont en français. Un commit dit ce qui change pour le joueur, puis pourquoi.
