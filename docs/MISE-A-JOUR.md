# Comment DrDXT se met à jour, et comment une version est publiée

## Ce que fait l'App

1. Au démarrage, l'App lit un petit fichier : `<racine>/updates/app/manifest`.
   La racine est `https://ablanchard-dev.github.io/drdxt` (le site GitHub Pages du dépôt public) ; la variable
   d'environnement `DRDXT_UPDATE_URL` la remplace, et posée à `off` elle coupe toute vérification (aucun réseau touché, le
   pied de page le dit). Un fichier absent (404) veut dire « rien à offrir », jamais une erreur.
2. Le manifeste est signé (Ed25519) par la clé de release du projet. La clé publique est dans l'App
   (`UpdateTrust.ProductionPublicKey`) ; elle ne peut que vérifier. Un manifeste non signé, mal signé, ou qui propose une
   version plus ancienne est refusé, et le refus est affiché : jamais tu, jamais installé.
3. Le paquet (l'installeur) n'est téléchargé qu'après la signature vérifiée ; son empreinte SHA-256 doit être celle du
   manifeste signé, sinon il n'est jamais lancé.
4. Une nouvelle version de l'App ne s'installe **jamais sans toi** : le pied de page propose « Installer », l'empreinte est
   relue juste avant le lancement, le fichier est verrouillé entre les deux, et l'installeur demande lui-même l'élévation.
5. Il n'y a pas de canal silencieux : tout ce qui change ce que DrDXT règle arrive dans une nouvelle version de l'App, que
   tu installes toi-même. Rien d'autre que ce manifeste et, sur ton accord, l'installeur ne quitte ni n'entre.
6. Tant que le mainteneur n'a publié aucun manifeste sur ce site, rien n'arrive et le pied de page dit « à jour ».

## Ce que fait le mainteneur (hors de l'App)

- La clé de release est créée une fois par `tools/publication/signer.cs` (`-- cle`). La graine privée est chiffrée pour le compte
  Windows du mainteneur (DPAPI) et ne quitte pas sa machine ; elle n'entre jamais dans le dépôt. Seule la clé publique est
  dans le code.
- Pour une version : l'installeur est construit, publié comme fichier d'une GitHub Release, puis
  `signer.cs -- manifeste App <version> <url du fichier> <installeur> <sortie>` écrit le manifeste signé, le relit avec la
  clé publique, et le manifeste est publié à `<racine>/updates/app/manifest` (GitHub Pages du dépôt, fichiers statiques).
- La clé publique du code est celle du mainteneur, créée le 09/10/2026 (empreinte hex
  `8fb9aa669956b185d6d64bd0fb2010365980962cd84b2fb77ad42ce10b2fe6eb`). La racine est écrite dans le code
  (`VerificationMiseAJour.UrlParDefaut`).
  Si une clé devait être remplacée, l'ancienne App ne reconnaîtrait plus les manifestes : une nouvelle version s'installe
  alors à la main depuis la Release.
- La graine privée est chiffrée pour le compte Windows du mainteneur ET par une phrase demandée à chaque signature : un
  programme qui tourne sous son compte ne la lit pas sans la phrase. Perdre la graine ou la phrase, c'est perdre la
  possibilité de signer : les joueurs devraient alors réinstaller à la main une version portant une nouvelle clé.
- Pas d'épinglage de certificat sur GitHub : ses certificats tournent ; la confiance repose sur la signature du manifeste,
  pas sur le transport.

## Ce qui n'est pas encore là

- L'installeur n'est pas signé Authenticode (demande de signature auprès d'une fondation de signature pour logiciels libres
  après la sortie publique). Tant qu'il ne l'est pas, Windows affiche son avertissement d'éditeur inconnu : l'empreinte du
  manifeste signé reste la preuve d'intégrité.
