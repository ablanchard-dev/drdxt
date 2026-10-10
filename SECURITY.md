# Sécurité

English below.

DrDXT tourne avec un helper administrateur et un service système. Un défaut de sécurité se signale en privé,
jamais dans une issue publique.

## Signaler une faille

Utilise le signalement privé de GitHub : onglet **Security** du dépôt, puis **Report a vulnerability**. Décris ce
qu'un attaquant obtient, avec quels droits au départ, et les étapes pour le reproduire. Le correctif et l'avis
public suivent, avec ton nom si tu le souhaites.

## Ce qui compte comme une faille

- Faire exécuter au helper ou au service une opération hors de leur liste blanche, ou une valeur hors des bornes.
- Obtenir des droits administrateur ou SYSTEM depuis un compte sans droits, par le tube nommé, le manifeste signé,
  le dossier `%ProgramData%\DrDxt`, le lanceur ou l'installeur.
- Faire accepter une mise à jour, un manifeste ou une clé qui ne sont pas signés par le projet.
- Faire sortir de la machine une donnée que la section « Ce qui quitte la machine » du README ne nomme pas.

## Ce que DrDXT protège déjà, et ses limites connues

La page [docs/SECURITE.md](docs/SECURITE.md) décrit chaque actif, l'attaquant visé, le test qui tient chaque ligne,
et les limites acceptées.

## Versions suivies

Seule la dernière version publiée reçoit les correctifs de sécurité.

---

# Security (English)

DrDXT runs with an administrator helper and a system service. A security issue is reported in private, never in a
public issue.

## Reporting a vulnerability

Use GitHub's private reporting: the repository's **Security** tab, then **Report a vulnerability**. Describe what an
attacker gains, with which rights at the start, and the steps to reproduce it. The fix and the public advisory
follow, with your name if you wish. French or English, either is fine.

## What counts as a vulnerability

- Making the helper or the service run an operation outside their allowlist, or a value outside its bounds.
- Gaining administrator or SYSTEM rights from an unprivileged account, through the named pipe, the signed manifest,
  the `%ProgramData%\DrDxt` folder, the launcher or the installer.
- Getting an update, a manifest or a key accepted that the project did not sign.
- Making data leave the machine that the README's "What leaves the machine" section does not name.

## What DrDXT already protects, and its known limits

[docs/SECURITE.md](docs/SECURITE.md) (in French) describes each asset, the attacker considered, the test that holds
each line, and the accepted limits.

## Supported versions

Only the latest released version receives security fixes.

