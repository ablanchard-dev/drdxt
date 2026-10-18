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

## Ce que DrDXT protège déjà

DrDXT a passé plusieurs revues adverses indépendantes : des relectures qui cherchaient à le casser, suivies chacune de
correctifs et de tests. Ce qui en sort, en bref :

- **Un seul programme reçoit les droits administrateur**, un lanceur natif sans moteur .NET. Il relance le reste avec un
  environnement reconstruit : un programme du compte ne peut pas glisser son code dans un processus élevé.
- **Le programme élevé n'exécute qu'une liste fermée d'opérations**, valeur par valeur, avec des bornes. Pas de chemin de
  registre libre, pas de commande libre, aucune écriture du BIOS.
- **Il ne parle qu'à l'application installée**, reconnue à son chemin et à son empreinte, par un canal refusé au réseau.
- **Chaque réglage lit sa valeur d'origine avant d'écrire** et sait la rendre ; un retour arrière laissé sur le disque
  ne rejoue que ce que son réglage écrit lui-même.
- **Les mises à jour sont signées** (Ed25519) : rien n'est téléchargé avant la signature, rien n'est lancé sans
  l'empreinte, et jamais sans ton clic.
- **Les dossiers sensibles appartiennent aux administrateurs**, et un dossier piégé d'avance (lien, autre propriétaire)
  est refusé.
- **Aucun secret dans le code**, et rien ne quitte la machine que le README ne nomme.
- **Les identifiants Windows ne vont qu'à un proxy configuré** (variable ou proxy manuel) : un proxy découvert par WPAD
  ne reçoit rien, même s'il les demande.

Le détail, menace par menace, avec le test qui prouve chaque ligne et les limites acceptées :
[docs/SECURITE.md](docs/SECURITE.md).

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

## What DrDXT already protects

DrDXT went through several independent adversarial reviews, each followed by fixes and tests. In short:

- **A single program receives administrator rights**, a native launcher with no .NET runtime. It relaunches the rest
  with a rebuilt environment: a program in the user's account cannot slip its code into an elevated process.
- **The elevated program runs only a closed list of operations**, value by value, within bounds. No free registry
  path, no free command, no BIOS write.
- **It talks only to the installed app**, recognized by its path and hash, over a channel refused to the network.
- **Every setting reads its original value before writing** and can restore it; a rollback file left on disk only
  replays what its own setting writes.
- **Updates are signed** (Ed25519): nothing is downloaded before the signature, nothing is launched without the hash,
  and never without your click.
- **Sensitive folders belong to administrators**, and a folder planted in advance (link, other owner) is refused.
- **No secret in the code**, and nothing leaves the machine that the README does not name.
- **Windows credentials go only to a configured proxy** (variable or manual proxy): a proxy discovered through WPAD
  gets nothing, even when it asks.

The details, threat by threat, with the test proving each line and the accepted limits:
[docs/SECURITE.md](docs/SECURITE.md) (in French).

## Supported versions

Only the latest released version receives security fixes.

