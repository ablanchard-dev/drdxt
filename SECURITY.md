# Sécurité

DrDXT tourne avec un helper administrateur et un service système. Un défaut de sécurité se signale en privé,
jamais dans une issue publique.

## Signaler une faille

Utilise le signalement privé de GitHub : onglet **Security** du dépôt, puis **Report a vulnerability**. Décris ce
qu'un attaquant obtient, avec quels droits au départ, et les étapes pour le reproduire. Le correctif et l'avis
public suivent, avec ton nom si tu le souhaites.

## Ce qui compte comme une faille

- Faire exécuter au helper ou au service une opération hors de leur liste blanche, ou une valeur hors des bornes.
- Obtenir des droits administrateur ou SYSTEM depuis un compte sans droits, par le tube nommé, le manifeste signé,
  le dossier `%ProgramData%\DrDxt` ou l'installeur.
- Faire accepter une mise à jour, un manifeste ou une clé qui ne sont pas signés par le projet.
- Faire sortir de la machine une donnée que la section « Ce qui quitte la machine » du README ne nomme pas.

## Ce que DrDXT protège déjà, et ses limites connues

La page [docs/SECURITE.md](docs/SECURITE.md) décrit chaque actif, l'attaquant visé, le test qui tient chaque ligne,
et les limites acceptées.

## Versions suivies

Seule la dernière version publiée reçoit les correctifs de sécurité.
