# DrDXT — rapport de verification

Destine a quelqu'un qui n'a pas suivi le travail : un relecteur, un contributeur, un joueur qui veut savoir
ce qui tient.

**La regle du document, et elle est mecanique** : chaque ligne « prouve » nomme un test qui existe,
et `python tools/nonuisance/check-guard-tests.py docs/RAPPORT-VERIFICATION.md` refuse un nom qui ne
se resout pas. Un rapport dont les preuves ne sont pas resolvables est une plaquette.

**Ce que ce document ne fait pas** : il ne juge pas si un test est BON. C'est le travail des
mutations, consignees dans le journal. Il etablit qu'une affirmation a un instrument, et lequel.

---

## 1. Ce qui est PROUVE, et par quoi

| domaine | ce qui est etabli | l'instrument |
|---|---|---|
| non-nuisance materiel | la seule ecriture de tension est la sous-tension du processeur (`SetCpuVoltageOffsetOp` : vers le bas seulement, jusqu'a `TensionCpu.PlancherMv`, volatile, rendue au redemarrage) ; la carte graphique ne recoit que decalage d'horloge, frequence maximale, limite de puissance et ventilateur, bornes par la liste blanche ; aucune operation n'ecrit une variable du firmware (le voyage vers son setup passe par `shutdown.exe /r /fw`) ; l'ensemble des operations est **ferme** : la liste exacte est celle du test | `ThePrivilegedSurfaceIsClosedGateTests` · `docs/NON-NUISANCE.md` (la table des sites d'ecriture, que `tools/nonuisance/count-write-sites.py` recalcule) |
| bornes GPU | coeur, memoire, limite de puissance et ventilateur sont bornes, et chaque borne meurt sous mutation | `GpuSafeBoundsTests` |
| refus par defaut | le registre, les services, les identifiants DRS et les plans d'energie sont en **liste blanche**, et le refus se prouve par son MOTIF, pas par son resultat | `OpAllowlistTests` |
| mise a jour | le manifeste est signe Ed25519, verifie par la clef **reellement embarquee** ; un octet modifie est refuse ; la couture client-serveur est testee de bout en bout | `UpdateManifestEndpointTests` · `TheClientReadsTheRealServerTests` |
| honnetete de l'ecran | aucune phrase n'affirme un etat que le produit ne calcule pas, et la coche verte du pied de page ne peut pas revenir | `TheScreenMayNotClaimAStateItNeverComputesTests` |
| ce que dit un constat | DrDXT est gratuit depuis le 23/09/2026 : tout constat nomme ce qu'il a trouve, aucun ne cache le reglage | `FindingViewModelTests.Every_finding_names_what_it_found` |
| rapport SAV | aucun chemin absolu, aucune cle de licence, aucune donnee nominative ; meme unite memoire que l'ecran | `DiagnosticReportTests` |
| memoire — topologie | le simple canal est dit quand il est prouve, et **tu** ; un emplacement illisible se DIT au lieu de se taire ; rien n'est affirme hors d'un poste fixe | `MemoryTopologyAdvisorTests` |
| memoire — stabilite | un profil XMP actif + des redemarrages sales se disent **sans accuser** et **sans chiffre** | `MemoryStabilityAdvisorTests` · `MemoryStabilityReachesTheScanTests` |
| memoire — etat de l'art | aucune affirmation chiffree sans source ET date ; les mythes sont prouves ABSENTS par l'API qui les implementerait | `TheRamStateOfTheArtHasNoUnsourcedClaimTests` |
| instruments | aucun plancher de bruit sans date ni nombre de passes ; un instrument jamais lance ici ne peut porter aucun chiffre | `TheInstrumentRegistryCannotRotTests` |
| securite serveur | aucun `CommandText` n'est assemble ; chaque avis de vulnerabilite porte sa ligne de tri | `TheVulnerabilityTriageIsCompleteGateTests` |

Le compte de la suite n'est pas recopie ici : le README publie celui du dernier passage, et
`tools/gates/compte_des_tests.py` refuse un compte qui n'est pas le sien.

---

## 2. Ce qui est UNKNOWN — nomme, pas cache

| ce qu'on ne sait pas | pourquoi |
|---|---|
| ce que DrDXT fait gagner en FPS sur une machine donnee | le rejeu in-game a un plancher de **6,46 %** : il ne resout qu'un gain > ~13 % (`docs/INSTRUMENTS.md`, I-05) |
| si le profil memoire du client est stable | on lit une frequence configuree, on ne teste rien |
| les timings reels (CL, tRCD, tRP, tRAS) et le rapport UCLK:MCLK | aucune API Windows ne les expose |
| si les 9 guides BIOS sont justes sur une carte donnee | **0 sur 9 verifies** — il faut les cartes |
| ce qu'un firmware inconnu fait d'un boot-to-setup | hors de portee par construction |
| si le produit survit a un no-boot puis a sa recuperation | aucun hyperviseur ni machine sacrifiable ici |

**La forme de ces lignes est le produit lui-meme** : DrDXT s'engage a ne pas affirmer ce qu'il n'a
pas mesure. Un rapport de verification qui cacherait sa propre colonne UNKNOWN contredirait ce
qu'il verifie.

---

## 3. Ce qui attend une decision

`GATES.md` porte le detail. Une seule decision reste, et elle n'est pas technique :

1. **Le certificat de signature** : sans lui, l'installeur declenche SmartScreen.

---

## 4. Le motif le plus couteux trouve dans ce depot

**Trois fois en une soiree, une donnee juste etait calculee, documentee comme decisive, et lue par
personne** : `DeviceLocator` et `Ranks` (lus depuis le 26/08), puis `IsLocked`.

Aucun test ne peut l'attraper : **un test verifie qu'une donnee est JUSTE, jamais qu'elle est LUE.**
Le seul instrument qui l'a trouve les trois fois est le comptage des lecteurs :
`grep -rn "<nom>" src/ | wc -l`, applique **aux vues** et non au code.

Et le corollaire, paye le meme soir : `SubScores` etait couvert par **six tests verts** et affiche
par **aucune vue**. Une propriete testee mais jamais affichee est pire que du code mort — la suite
verte la fait paraitre vivante.
