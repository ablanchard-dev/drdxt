# NON-NUISANCE — ce que DrDXT peut ecrire sur la machine du client

> Ecrit le 05/09/2026 par la boucle de livraison, lot LD.
>
> Ce fichier existe pour repondre a une question qu'un acheteur a le droit de poser :
> **est-ce qu'une branche de ce logiciel peut abimer mon materiel, casser mon BIOS ou mon Windows ?**
>
> La reponse ne peut pas etre « non, promis ». Elle se construit en trois temps : dire ce que le
> produit PEUT demander, dire ou il ecrit reellement, et prouver que chaque garde annoncee peut
> echouer.

## Les trois classes de nuisance

| Classe | Ce que ca veut dire | Reversible ? |
|---|---|---|
| **H1 — materiel** | une carte, une puce ou un module abime | **non** |
| **H2 — BIOS** | machine qui ne demarre plus, etat firmware modifie | par Clear CMOS / flashback |
| **H3 — Windows** | ne demarre plus, ne se met plus a jour, securite affaiblie | par point de restauration |

## Ce qui est deja etabli, et prouve par mutation

- **La surface privilegiee est FERMEE a 20 operations** (`ThePrivilegedSurfaceIsClosedGateTests`).
  Tout ce qui s'execute avec des droits d'administrateur passe par le tube nomme du helper, et ce
  tube ne transporte que des sous-types de `PrivilegedOp`. Un 21e op fait rougir deux gardes.
  La 19e, ajoutee le 10/09/2026 : **`DeleteNvApiDrsSettingOp`**, l'inverse d'une ecriture DRS au
  profil d'un jeu (detail dans la table H3). La 20e, ajoutee le 11/09/2026 : **`CommitManifestOp`**,
  l'ecriture du manifeste signe par le helper (detail dans H-AUCUNE) — les deux gardes ont rougi,
  comme elles le doivent, avant qu'elle entre.
- **Aucune operation n'ecrit une tension.** H1 est donc tenu par **absence**, pas par garde — la
  forme la plus solide, et la plus facile a perdre en silence. Deux controles la gardent
  (`NoVoltageWriteOpExistsGateTests`, et le controle de nommage ci-dessus).
- **`DrDxt.Bios` ne peut pas ecrire** : il ne reference que `DrDxt.Core`. Le composant qui envoie
  l'utilisateur dans son BIOS n'a aucun moyen d'y toucher (`TheGuideNeverWritesTheBiosGateTests`).
- **Aucune operation n'ecrit dans le firmware** : la seule est une LECTURE. Depuis L3.1 (27/08),
  elle lit les varstores `Setup` (AMI) et `CpuSetup` (Intel), noms fermes dans l'allowlist ;
  corrige le 25/09/2026, cette ligne disait « le varstore Setup est refuse », ce qui n'etait plus vrai.
- **Le revert DRS restaure la portee qu'il a modifiee** (`DrsRevertMatchesItsScopeGateTests`).
- **Les 4 rails GPU tiennent, ecretage ET refus** — 8 mutations le 06/09, 8 rouges, 0 survivant.
  Le detail rail par rail, avec le nom du test qui tue chaque mutation, est dans la table H1.

---

## LIGNE ZERO — les motifs cherches

Ce controle compte des appels d'API. **Une ecriture par une API absente de cette liste lui est
invisible** : `WMI Put`, `SetupApi`, `DeviceIoControl`, un P/Invoke direct. Le trou est nomme ici
plutot que masque, et il s'agrandit chaque fois qu'on ajoute un motif — jamais l'inverse.

```
SetFirmwareEnvironmentVariable   NvAPI_GPU_Set*   Registry.SetValue   .SetValue(
bcdedit.exe   shutdown.exe   sc.exe   File.Write*   File.AppendAll*   Process.Start
TrySet*   TryDelete*   ChangeDisplaySettings*(   .PriorityClass =   PowerWrite*(   PowerSetActiveScheme(
PowerSetUserConfigured*(
```

**Deux corrections de cet instrument le 05/09, et elles disent quelque chose.**

1. Le motif etait `shutdown` tout court : il comptait le MOT, donc des commentaires expliquant
   `shutdown /r /fw`, une phrase sur le demarrage rapide, un « unwind cleanly on shutdown ». Neuf
   fichiers etaient listes comme sites d'ecriture sans l'etre. **Un instrument qui SUR-compte
   trompe autant qu'un qui sous-compte** : il fait relire du vide, et on apprend a l'ignorer. Les
   lignes de commentaire sont desormais ignorees, et la vraie invocation s'ecrit `shutdown.exe`.

2. Plus grave : **les ecritures GPU ne portaient aucun motif de la liste**. Elles s'appellent
   `TrySetCoreClockOffset`, `TrySetPowerLimitPercent`, `TrySetDrsGlobalSetting`, `TrySetFanCurve` —
   l'API NVAPI/ADLX est enveloppee, donc son nom n'apparait pas. **24 des 53 sites, c'est-a-dire
   toute la surface qui touche au MATERIEL, etaient invisibles.** Le motif `TrySet*` est un motif
   d'INTENTION, et c'est ce qu'il faut quand l'API est enveloppee.

## Les sites, recalcules

### 19/09/2026 — la table ne decrivait plus le code, et personne ne l'a vu

Lance ce soir a la main, ce script sort non-zero : **dix ecarts**, dont **six fichiers absents
de la table**. Un document qui enumere tout ce que le produit ecrit sur la machine d'un client,
et qui a pris du retard sans que rien ne le dise, est exactement la dette qu'il existait pour
empecher.

**Et il est branche dans la CI** -- je croyais le contraire en commencant, mon crible excluait
les tirets du nom de fichier. Ce qui explique le retard est ailleurs, et c'est mesurable : le
depot est **183 commits en avance sur `origin/main`**, et rien n'est pousse (decision du mainteneur).
Un gate cable mais jamais execute garde exactement autant qu'un gate absent. La seule chose qui
pouvait attraper cette derive etait quelqu'un qui lance le script a la main.

Les six, et ce qu'ils ecrivent :

| Fichier | Ce qu'il ecrit | Ou |
|---|---|---|
| `Updates/InstallationMiseAJour.cs` | copie le paquet verifie, puis supprime la copie de travail | dossier de staging de l'app |
| `ViewModels/LicenseViewModel.cs` | ouvre le navigateur sur la page d'achat (`Process.Start`) | rien sur le disque |
| `Autopilot/JournalDesCandidats.cs` | ajoute une ligne au journal des leviers essayes | donnees locales de l'app |
| `ElevatedHelper/ModeDesinstallation.cs` | ecrit le rapport de desinstallation, puis rend les reglages | donnees locales + registre |
| `Reconciler/Manifest/MachineManifestCommit.cs` | ecrit le manifeste signe, puis le deplace en place (atomique) | ProgramData |
| `Reconciler/Manifest/MachineManifestKey.cs` | ecrit la clef de machine, meme ecriture atomique | ProgramData |

Aucun ne sort du perimetre deja decrit plus haut — donnees locales, ProgramData, registre par
l'assistant eleve — et aucun ne touche H1 ni H2. Ce qui manquait n'etait pas une protection,
c'etait la **phrase qui les nomme**.

### 26/09/2026 — treize ecarts de plus, releves un par un

Meme cause qu'au 19/09 : rien n'est pousse, donc la CI ne tourne pas, donc personne n'a vu la table
prendre du retard. Relance a la main : **treize fichiers** ne correspondaient plus. Chaque ligne
ci-dessous a ete lue dans le code, pas deduite du motif.

Ce qui est apparu :

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/App.xaml.cs` (4 → 7) | le journal de plantage (ajout, jamais ecrasement) ; une sonde de gel et les fichiers temoins du mode photos hors ecran, **diagnostics seulement** | `%TEMP%` et donnees locales ; rien sur la machine |
| `ViewModels/ScanViewModel.Agir.cs` (0 → 2) | la garde d'annulation BCD ; la **console de reparation Windows**, sur clic et sous UAC : `DISM /RestoreHealth` puis `sfc /scannow`, outils de Windows, visibles et fermables | donnees locales ; H3, outils Microsoft, geste du joueur |
| `Application/Tweaks/JournalDesLeviersAppliques.cs` (0 → 1) | le journal des leviers gardes (source des constats « gain disparu ») ; n'ecrase jamais un journal illisible | donnees locales |
| `Detection/Repair/RepairAuditService.cs` (1 → 2) | `bcdedit /enum {current}` : **lecture seule** | rien d'ecrit |
| `ElevatedHelper/Execution/SanteDesComposants.cs` (0 → 1) | `dism /CheckHealth` : **lecture seule** | rien d'ecrit |
| `ViewModels/BiosGuideViewModel.cs` (0 → 1) | 07/10/2026 (D12) : ouvre la page d'assistance MSI de la carte dans le navigateur, **sur clic** | rien d'ecrit ; adresse https://www.msi.com seulement, nom de carte filtre |
| `ElevatedHelper/Execution/Win32BcdActions.cs` (0 → 2) | `bcdedit /set` et `/deletevalue` sur `{current}` | H3 ; liste fermee de **5 elements** et de leurs seules valeurs documentees, dans l'allowlist du helper (`OpAllowlistTests`) |
| `ElevatedHelper/Execution/Win32NetworkQosActions.cs` (0 → 1) | une politique QoS (plafond d'envoi) par PowerShell, dans le **magasin actif** de Windows | effacee au redemarrage ; posee sous bail, retiree a la fermeture du jeu et a l'arret du helper |
| `Measurement/Frames/CompteurPresentsDxgi.cs` (0 → 1) | `logman start/stop` : une session de trace ETW temporaire | arretee en fin de mesure, et nettoyee si une interruption l'a laissee ouverte |

Ce qui a disparu, et pourquoi :

| Fichier | Raison |
|---|---|
| `ViewModels/LicenseViewModel.cs`, `Licensing/DpapiLicenseCache.cs`, `Licensing/LicenseAntiRollbackStore.cs`, `Uninstall/UninstallProdSeams.cs` (1 → 0 chacun) | DrDXT est gratuit depuis le 23/09/2026 : licence, paiement et leurs ecritures sont partis avec le module `DrDxt.Licensing` |
| `Reconciler/Manifest/MachineManifestCommit.cs` (1 → 0) | l'ecriture n'a pas disparu, elle a demenage : le commit passe par `ProgramDataManifestStorage`, deja dans cette table |

Aucun nouveau site ne touche H1 (materiel). Le seul qui touche le demarrage (`Win32BcdActions`)
etait deja borne par l'allowlist ; ce qui manquait, encore, etait la phrase qui le nomme.

### Et l'instrument sur-comptait

Deux des ecarts n'etaient pas des ecritures du tout : `\.SetValue\(` attrapait le setter WPF
de `Effets/Controles.cs`, qui ne touche rien sur la machine. L'en-tete de ce script nomme deja
la faute -- « un instrument qui SUR-compte trompe autant qu'un qui sous-compte : il fait relire
du vide, puis on apprend a l'ignorer ». Le motif demande desormais un destinataire
(`key.SetValue(`, `_registry.SetValue(`) : les cinq vraies ecritures de registre restent, les
deux fausses disparaissent.


### 06/10/2026 — six ecarts, et un site que l'instrument ne voit pas

Meme cause qu'au 26/09 : la CI etait rouge plus tot dans le meme job, donc cette etape ne
tournait pas. Chaque ligne a ete lue dans le code.

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/AppServiceCollectionExtensions.cs` (2 → 3) | relance de DrDXT **en administrateur** pour enchainer une analyse (`--analyser --scene`), sur clic, sous UAC | le joueur accepte ou refuse l'UAC ; rien d'ecrit |
| `Application/Tweaks/JournalDesLeviersAppliques.cs` (1 → 2) | retrait d'une entree du journal des leviers gardes, quand un levier est annule | donnees locales |
| `Application/Tweaks/PreuveDuRepos.cs` (0 → 2) | la preuve au repos du reglage de la carte (debut et resultat), ecrite par fichier temporaire puis remplacement | donnees locales |
| `Detection/Gpu/MagasinDePilotes.cs` (0 → 1) | `pnputil /enum-drivers` : **lecture seule** | rien d'ecrit |
| `Detection/Repair/InventaireMachine.cs` (0 → 1) | la reference de l'inventaire (ce qui est installe), pour dire ce qui a change depuis le dernier scan | donnees locales |
| `ElevatedHelper/Execution/Win32RestorePointActions.cs` (1 → 2) | **`vssadmin resize shadowstorage /maxsize=5%` sur le disque systeme** : la place que Windows reserve aux points de restauration, pour qu'un point puisse etre cree | **ecriture systeme** sous le helper eleve, avant la creation d'un point, **seulement quand aucune reserve n'existe** (lue par WMI `Win32_ShadowStorage`) : une reserve deja fixee n'est jamais modifiee (revue F-13, 07/10/2026) ; celle que DrDXT cree reste a la desinstallation |

Hors de l'instrument : `App/Platform/ReleveAuDemarrage.cs` (J1b) cree le raccourci
`DrDXT releve.lnk` dans le dossier Demarrage **du joueur** (objet COM `WScript.Shell`, qu'aucun motif
ne voit), pour que le releve des parties tourne DrDXT ferme. Pose seulement si DrDXT est installe ;
retire quand le releve est eteint et a la desinstallation (`[UninstallDelete]`).
**Limite (revue F-11, 07/10/2026)** : la desinstallation tourne sous le compte qui a valide l'UAC. Un joueur
sans droits qui a fait valider l'UAC par un autre compte garde le raccourci dans SON Demarrage, pointant vers
un exe efface (rien ne se lance ; ce que Windows affiche alors n'a pas ete observe). A retirer a la main : `shell:startup`.

### 07/10/2026 — « Exporter ma machine » (kit testeur, T1)

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/AppServiceCollectionExtensions.cs` (3 → 4) | un fichier `machine-exportee-<date>.json` : inventaire, reglages et constats du docteur, au format de `tests/Shared/machines`, anonymise (`MachineExportee.CeQuiEstRetire`, prouve par `ExportDeMachineTests.Aucune_valeur_qui_designe_le_joueur_ou_son_pc_ne_sort`) | **Documents du joueur**, sur clic seulement ; le nom porte la date a la seconde, donc un clic ne remplace qu'un export de la meme seconde ; rien n'est envoye sur le reseau, rien n'est touche sur la machine (lecture seule) |

### 08/10/2026 — « Vérifier DrDXT sur mon PC » (T1b)

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/AppServiceCollectionExtensions.cs` (meme site que l'export, `EcrireDansDocuments`) | un fichier `verification-drdxt-<date>.txt` : le bilan, puis chaque reglage pose, relu, rendu, relu, avec sa valeur d'origine ; sans le nom du PC | **Documents du joueur**, apres son accord a l'ecran |
| `Application/Autopilot/ValidationDeLaMachine.cs` (0, ecrit par les leviers) | chaque levier de `ValidationComplete.Leviers`, par **le canal eleve du produit** (helper, accord de Windows sur le clic du joueur), puis rendu aussitot | jeu ferme exige, refuse si un essai est deja au journal ; journal de remise du produit (`pending-revert.json`) pose AVANT chaque ecriture, rejoue au demarrage si l'App meurt ; arret au premier levier non rendu ; aucun point de restauration |

### 08/10/2026 — les réglages de la carte gardés par le joueur sont rendus à la désinstallation (revue n° 10)

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `ElevatedHelper/ModeDesinstallation.cs` (client en processus) | a la desinstallation, l'**origine** d'une limite de puissance (NVIDIA ou AMD), d'Anti-Lag, de FRTC ou de la Vsync du pilote AMD gardes par le joueur (`NvApiTuningActions`, `ADLXTuningActions`, une seule instance ADLX pour lire et ecrire) | seulement si la carte porte ENCORE la valeur que DrDXT a posee (relue par `LectureDesCiblesWin32`) ; sinon laissee telle quelle. Limite connue (revue n° 11) : la valeur posee ne porte pas l'identite de la carte ; une carte CHANGEE avant la desinstallation, reglee par le joueur a la meme valeur, serait rendue. Le rendu refuse en dur frequence, tension et ventilateur (`RenduALaDesinstallation.Juger`). `OpAllowlist.ValiderOrigine` n'admet jamais frequence, tension ni ventilateur (sous-tension ADLX, decalage NVIDIA refuses) |

### 07/10/2026 — L-ECRAN, un site que l'instrument ne voyait pas, et le motif qui le voit

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `Detection/Display/Win32ModeEcran.cs` (0 → 2) | le **mode de l'ecran de jeu** (`ChangeDisplaySettingsEx`, user32) : seule la frequence change, vers la plus haute que Windows enumere a la resolution courante | **sans droits** : sans `CDS_GLOBAL`, le mode est range dans le profil de l'utilisateur (Microsoft Learn). Rien n'est ecrit si `CDS_TEST` refuse ; le changement est d'abord dynamique (non enregistre) ; relecture, puis le joueur garde le mode dans les 15 s, sinon l'ancien revient ; enregistre seulement apres. Annulation : l'original lu avant, remis et relu |

C'est un P/Invoke direct, le trou que la ligne zero nomme : le motif `ChangeDisplaySettings\w*\(` est
ajoute (la parenthese evite de compter le nom cite dans une source ou une phrase). Preuves :
`EcranFrequenceTweakTests.Mode_refuse_a_l_essai_rien_n_est_ecrit`,
`EcranFrequenceTweakTests.Relecture_qui_ne_confirme_pas_ramene_l_ancien_mode`,
`EcranFrequenceTweakTests.Sans_confirmation_dans_le_delai_l_ancien_mode_revient`, et le tour complet
appliquer/annuler de `ChaqueLevierDuProduitSaitSeDefaireTests`. Ce que le banc ne prouve pas : qu'un
vrai ecran affiche le mode que le pilote accepte. C'est la confirmation du joueur qui le tient.

### 07/10/2026 — L-VOISINS, la priorite des programmes voisins, que l'instrument ne voyait pas

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/Platform/CalmeurDeVoisins.cs` (0 → 1) | la **priorite d'un autre processus** (`Process.PriorityClass`), de « normale » a « en dessous de la normale », pendant une partie | **sans droits**, rien d'ecrit sur le disque : Windows ne garde pas cette priorite, le processus la perd a son redemarrage. Seulement apres le clic du joueur (propose d'office d'apres les parties relevees, jamais allume seul) ; seulement un voisin a plus d'un quart de coeur sur deux lectures ; jamais le jeu, la fenetre au premier plan, un processus de Windows ou de securite, DrDXT, un antitriche (liste plus tout nom contenant « anticheat »), ni ce que le joueur a exclu (liste visible et modifiable sur l'ecran, `voisins-exclus.json`) ; seule une priorite « normale » est touchee. Rendue a la fin de la partie, a l'arret et a la fermeture de DrDXT si personne ne l'a changee. Sa bascule est datee au journal des changements et jugee sur les parties ; « moins bien en jeu » l'eteint seul |

Le setter .NET n'appartenait a aucun motif : `\.PriorityClass\s*=(?!=)` est ajoute. Preuves :
`CalmeurDeVoisinsTests.Ni_le_jeu_ni_le_premier_plan_ni_le_systeme_ne_sont_touches`,
`CalmeurDeVoisinsTests.Ni_un_antitriche_ni_un_programme_exclu_par_le_joueur_ne_sont_calmes`,
`CalmeurDeVoisinsTests.Une_priorite_deja_changee_par_quelqu_un_d_autre_n_est_ni_touchee_ni_rendue`,
`CartesAgirTests.Le_calmeur_de_voisins_est_date_juge_et_retire_seul_s_il_coute`. EcoQoS
(`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) n'est **pas** pose : la source Microsoft le reserve au travail qui ne
compte pas pour l'utilisateur, et un voisin en partie (diffusion, appel) en est souvent un.
Limite : si DrDXT est tue en pleine partie, le voisin garde sa priorite basse jusqu'a son prochain redemarrage.

### 07/10/2026 — L-CPU, le mode de boost du processeur, et le changement de plan que l'instrument ne voyait pas

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `ElevatedHelper/Execution/Win32PowerPlanActions.cs` (0 → 5) | `PowerSetActiveScheme` (le changement de plan de `fixit.power_plan`, present depuis M2 et jamais compte) ; et depuis ce jour `PowerWriteACValueIndex` : la **valeur secteur de PERFBOOSTMODE** d'un plan (`SetProcessorBoostModeOp`), puis la reactivation du meme plan s'il est actif (Microsoft Learn : sans elle, la valeur du plan actif ne prend pas effet) | **helper eleve**. Un seul reglage, fixe par le type d'op ; valeur 0 a 4 (celles que Microsoft documente ; 5 et 6, enumerees par powercfg, refusees) ; plan non vide. Le plan actif est lu avant d'ecrire : non lu, rien n'est ecrit ; un plan qui n'est pas actif n'est jamais active. La valeur sur batterie n'est jamais touchee. Propose sur un PC fixe ou un portable (depuis le 08/10/2026, LAP1 : seule la valeur secteur est ecrite), jamais sur une console portable, quand la valeur lue vaut 0 et que le defaut de la personnalite du plan vaut 1 a 4 ; l'original est relu juste avant, l'annulation le rend ; mesure en jeu (AutoHot), rendu s'il ne gagne rien ; ferme par les arrets brutaux (surface Processeur) |

Preuves : `OpAllowlistTests.Mode_de_boost_refuse_une_valeur_forgee`, `OpAllowlistTests.Mode_de_boost_refuse_un_plan_vide`,
`ModeBoostTweakTests.Relu_juste_avant_et_deja_rendu_par_quelqu_un_rien_n_est_ecrit`,
`ModeBoostTweakTests.Jamais_sur_une_console_portable`, `ModeBoostTweakTests.Sur_un_portable_le_boost_secteur_est_propose`,
`MachinesEnregistreesTests.Chaque_machine_ne_recoit_aucun_retour_du_boost_invente`, et le tour complet appliquer/annuler
de `ChaqueLevierDuProduitSaitSeDefaireTests`. Residuel, dit : le banc ne prouve pas que Windows applique la valeur au
processeur, ni son effet ; Microsoft se contredit sur cet effet avec HWP (page Server « only applies to Non-HWP systems »,
page OEM « Disabled » aussi en mode autonome). La mesure en jeu le tranche. Garde apres la mesure, son origine entre au manifeste en
`RevertOnly` : la desinstallation la rend seulement si la valeur relue est encore celle que DrDXT a posee
(`LectureDesCiblesWin32`, `PowerReadACValueIndex`), comme les autres leviers.

### 08/10/2026 — ROAD1, les leviers vérifiés sur ce PC

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `App/App.xaml.cs` (7 → 10) | `drdxt-verification.txt`, `drdxt-expert.txt` et `drdxt-expert-crash.txt` dans `%TEMP%` : les bilans de « verifier-auto », « expert-auto » et « expert-crash » | **compilation de mise au point seulement** (`#if DEBUG`) : absent du produit livré ; mode qui relance « Vérifier DrDXT » hors écran |
| `App/ViewModels/ScanViewModel.Verifier.cs` (0 → 1) | ouvre le navigateur par défaut (`Process.Start` d'une adresse https) sur un ticket GitHub pré-rempli : CONTRIB1, « Envoyer mon rapport » | **App**, sans droits, sur le clic du joueur seulement ; rien n'est envoyé par DrDXT, le joueur relit et soumet ; l'adresse est construite par `LienDeRapport` (dépôt fixe, longueur bornée) |
| `Application/Tweaks/LeviersVerifies.cs` (0 → 1) | `leviers-verifies.json` : la liste des identifiants de leviers que « Verifier DrDXT sur mon PC » a poses, relus, rendus et relus sur cette machine | **App**, sans droits administrateur, dans le dossier de donnees de DrDXT (`DossierDonnees`). Ecrit seulement apres une verification lancee par le joueur ; cumule ; illisible, la liste est vide (rien n'est dit verifie sans preuve). Aucune ecriture systeme |

### 08/10/2026 — GPU-OC, le ventilateur NVIDIA en mode Expert (NVML)

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `GpuNative/Nvml/NvmlNative.cs` (0 → 2) et `GpuNative/Nvml/INvmlNative.cs` (0 → 2, les membres par defaut qui rendent `false`) | `nvmlDeviceSetFanSpeed_v2` (une vitesse fixe, la politique passe en manuel) et `nvmlDeviceSetDefaultFanSpeed_v2` (le retour a l'automatique), exports documentes de NVML (docs.nvidia.com/deploy/nvml-api, Maxwell et plus, droits administrateur) | **helper eleve** seulement, par `NvmlVentilateurActions` |
| `GpuNative/PawnIo/PawnIoIntel.cs` (0 → 1) | `ioctl_write_msr` du module IntelMSR de PawnIO (namazso/PawnIO.Modules 0.2.11, qui n'accepte d'ecrire que ses registres de puissance et de tension) : la **limite de puissance du processeur Intel** (MSR 0x610, `SetCpuPackagePowerLimitOp`), B1 du 10/10/2026. PL1 seule (bits 15:0), jamais PL2, fenetre, courant, tension ni verrou ; relu avant (le registre porte encore l'origine, pas de verrou BIOS) et apres (le registre porte ce qui a ete ecrit, sinon echec dit). Volatil : le BIOS remet sa valeur au redemarrage | **helper eleve** seulement, par `PawnIoCpuPowerActions` ; borne par `OpAllowlist` (`PawnIoIntel.NeToucheQuePl1` : PL1 entre 1 et la PL2 que la carte porte deja) ; le service de demarrage ne la recoit pas ; PawnIO jamais installe par DrDXT |
| `ElevatedHelper/Execution/NvmlVentilateurActions.cs` (0 → 2) | la pose d'une vitesse fixe (`ApplyGpuFanCurveOp` a un seul point) et le retour a l'automatique (`RestoreGpuFanAutoOp`) | **helper eleve**, mode Expert sur geste du joueur seulement. NVIDIA previent qu'une vitesse trop basse peut abimer la carte : DrDXT ne pose qu'en partant de l'automatique, jamais sous la vitesse que l'automatique vise a cet instant ni sous le minimum de la carte, et jamais sous 40 % (plancher a chaud de `OpAllowlist`). Tout est relu apres l'ecriture ; un echec rend l'automatique. Le retour se prouve par la politique relue (un retour rate a ete signale en 2022 sur le forum NVIDIA) : non confirme, l'echec le dit. Une courbe a plusieurs points reste refusee |

### 07/10/2026 — revue F-28, le mode d'alimentation de Windows 11 que l'activation d'un plan peut remettre

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `ElevatedHelper/Execution/Win32PowerPlanActions.cs` (5 → 6) | `PowerSetUserConfiguredACPowerMode` : le **mode d'alimentation sur secteur** (Meilleure autonomie, Equilibre, Meilleures performances ; autrefois « overlay »), seulement pour le **reposer** quand `PowerSetActiveScheme` (changement de plan de `fixit.power_plan`, reactivation apres PERFBOOSTMODE) l'a fait bouger | **helper eleve**, dans la meme op que l'activation. Le mode est lu avant (`PowerGetUserConfiguredACPowerMode`) ; inchange apres l'activation, rien n'est ecrit ; non lu (avant Windows 11), rien n'est ecrit ; repose, il est relu, et un mode qui ne revient pas fait echouer l'op avec la raison. Seule la valeur lue avant est reposee, jamais une autre. La valeur sur batterie n'est jamais touchee |

Source : Microsoft Learn documente les deux fonctions (powrprof.h, Windows 11 et Windows Server 23H2) : « Power modes were
previously referred to as "overlays" or "overlay schemes" » ; trois valeurs seulement, 961cc777-… (meilleure autonomie),
00000000-… (equilibre), ded574b5-… (meilleures performances). `PowerGetEffectiveOverlayScheme` et
`PowerGetActualOverlayScheme` existent dans powrprof.dll mais ne sont pas documentees : elles ne servent pas. Lu sur le PC
de reference le 07/10/2026, sans rien ecrire : plan actif Hautes performances, mode secteur et batterie 00000000-…
(equilibre), `ActiveOverlayAcPowerScheme` vide. Le risque n'y est donc pas observe ; la garde vaut pour les machines en
Equilibre avec un mode choisi. Preuves : `ModeDAlimentationGardeTests` (powrprof factice qui remet le mode a l'activation).

### 12/10/2026 — la tension des cœurs du processeur vers le bas (décision du mainteneur : « undervolt OK si la faille est vraiment fermée »)

| Fichier | Ce qu'il ecrit ou lance | Ou, et ce qui le borne |
|---|---|---|
| `GpuNative/PawnIo/PawnIoIntel.cs` (1 → 2) | `ioctl_write_msr` sur la **boite aux lettres OC** (MSR 0x150, `EchangerBoiteOc`) : la commande de lecture 0x10 et la commande d'ecriture 0x11 du **decalage de tension des cœurs et du cache** (`SetCpuVoltageOffsetOp`), **relatif a la tension que le BIOS avait mise a ce demarrage** (retenue dans HKLM au premier passage : 0 rend exactement l'origine, jamais au-dessus, jamais sous −125 mV au total ; relecture du 12/10 : en absolu, un undervolt deja en place etait remonte puis efface). Codage source : erpalma/throttled (`calc_undervolt_msr`) ; les bits 20:0 lus juste avant (ratio, tension cible, mode du BIOS) sont recopies, jamais remis a zero. Volatil : rendu au redemarrage | **helper eleve** seulement, par `PawnIoCpuPowerActions.SetVoltageOffset` ; borne par `OpAllowlist` (0 a −125 mV, jamais vers le haut) et par `TensionCpu.CommandeEcriture` (plans cœurs et cache seulement, lecture d'avant acceptee) ; **refuse si le processeur a SGX** (CPUID 7.EBX[2]) |
| `Application/Tweaks/JournalDesLeviersAppliques.cs` (2 → 0) | plus rien en direct : ses deux ecritures passent par l'ecriture atomique commune (`FichierJsonAtomique`, audit du 12/10, `7f02a27d`) | donnees locales |
| `ElevatedHelper/Execution/Adlx/ADLXTuningActions.cs` (2 → 3) | le **retour arriere** d'une ecriture de frequence de la carte AMD refusee qui a quand meme pris (meme API `TrySetGfx`, la valeur lue avant), ajoute le 12/10 (`0ea8dd65`) | **helper eleve**, dans la meme op ; n'ecrit que la valeur relue juste avant |

Classe de nuisance : une tension trop basse fait **planter** (ecran bleu, jeu qui se ferme), elle n'abime pas le materiel ; seule
une tension trop haute l'abime, et l'op ne monte jamais (refus par la liste blanche et par le codage, prouve par mutation :
`TensionCpuTests`, montee admise ⇒ rouge). Plundervolt (CVE-2019-11157, INTEL-SA-00289) n'attaque que les enclaves SGX ; Intel
l'a corrige en verrouillant la tension quand SGX est actif ; DrDXT refuse toute ecriture sur un processeur qui a SGX
(`PawnIoTensionTests`, SGX ⇒ aucune ecriture). Undervolt Protection (12e-14e gen, reglage BIOS) ou la securite par
virtualisation de Windows peuvent ignorer l'ecriture : chaque plan est relu, une valeur non retrouvee est un echec dit et un plan
deja pose est rendu (`PawnIoTensionTests`). Residuel : un decalage trop fort peut faire planter avant d'etre juge ; la garde
d'incident rend les leviers poses apres un arret, et le redemarrage le rend de toute facon. Lu sur le PC de reference le 12/10
(lecture 0x10 seulement) : decalage 0 sur les cœurs et le cache, processeur sans SGX.

### La frontiere reelle : chaque op doit rester inoffensive meme demandee par un programme hostile du compte

Le tube du helper n'admet que le compte courant, les administrateurs et SYSTEM, et refuse le reseau ; le helper verifie que
l'appelant est l'image de DrDxt.App. Mais l'App non elevee tourne au meme niveau d'integrite que n'importe quel programme du
compte, qui peut s'y injecter et parler au helper : aucun intermediaire eleve qui sert le meme compte n'echappe a cela (Microsoft
ne tient pas UAC pour une frontiere de securite). La seule frontiere qui tient est donc la forme fermee des ops et leurs bornes
(`OpAllowlist`, et les gardes relues a l'execution) : **toute op, presente ou future, doit rester sans dommage durable meme
appelee par un programme hostile du compte**. Exemples tenus : la tension seulement vers le bas et relative a l'origine du
demarrage, jamais avec SGX ; la PL1 jamais sous le cinquieme de la PL2 ; aucune ecriture de variable firmware. Et ce qui
contournerait toute borne (un programme eleve qui charge du code choisi par le compte) est traite a part : la tache planifiee
elevee passe par le lanceur natif qui reconstruit l'environnement (12/10/2026).

**Limite assumee, reglages DRS du pilote NVIDIA.** Une valeur DRS est bornee a l'enumeration que NVIDIA documente
(`ValeursDrs`, NvApiDriverSettings.h), pas plus strictement : un inverse rejoue la valeur LUE chez le joueur (synchro
« adaptative demi-frequence », plafond bas), et une borne plus stricte rendait cette origine irrejouable, le reglage de DrDXT
pose pour toujours. Un programme hostile du compte peut donc poser une valeur documentee mais genante sur le profil de base
(un plafond d'images tres bas) : nuisance reversible par le joueur dans le panneau NVIDIA, sans dommage durable. Le levier,
lui, n'ecrit pas quand l'origine lue sort de l'enumeration.

NvApiTuningActions passe de 2 a 4 sites : une ecriture que le pilote ramene a sa borne ou arrondit n'est plus laissee en
place, l'origine lue avant est reposee (puissance, decalage du coeur) ; deux ecritures de RENDU, vers une valeur deja lue
sur la carte.

`python tools/nonuisance/count-write-sites.py` refait ce compte et **sort non-zero** si un fichier
apparait, disparait, ou change de compte. Le script ne recopie rien : un chiffre ecrit a la main
est une dette tant qu'aucun outil ne le refait.

```counts
src/DrDxt.App/App.xaml.cs 10
src/DrDxt.App/AppServiceCollectionExtensions.cs 2
src/DrDxt.App/Platform/CalmeurDeVoisins.cs 1
src/DrDxt.App/Platform/RelanceElevee.cs 1
src/DrDxt.App/Updates/InstallationMiseAJour.cs 1
src/DrDxt.App/ViewModels/BiosGuideViewModel.cs 1
src/DrDxt.App/ViewModels/ScanViewModel.Agir.cs 2
src/DrDxt.App/ViewModels/ScanViewModel.Verifier.cs 1
src/DrDxt.Application/Autopilot/JournalDesCandidats.cs 1
src/DrDxt.Application/Parties/VerrouDuReleve.cs 1
src/DrDxt.Application/Tweaks/LeviersVerifies.cs 1
src/DrDxt.Application/Tweaks/PreuveDuRepos.cs 2
src/DrDxt.Core/Storage/FichierJsonAtomique.cs 1
src/DrDxt.Detection/Display/Win32ModeEcran.cs 2
src/DrDxt.Detection/Gpu/MagasinDePilotes.cs 1
src/DrDxt.Detection/Repair/InventaireMachine.cs 1
src/DrDxt.Detection/Repair/RepairAuditService.cs 2
src/DrDxt.ElevatedHelper/Execution/Adlx/ADLXTuningActions.cs 3
src/DrDxt.ElevatedHelper/Execution/NvApi/NvApiDrsActions.cs 3
src/DrDxt.ElevatedHelper/Execution/NvApi/NvApiTuningActions.cs 4
src/DrDxt.ElevatedHelper/Execution/NvmlVentilateurActions.cs 2
src/DrDxt.ElevatedHelper/Execution/PrivilegedOpExecutor.cs 1
src/DrDxt.ElevatedHelper/Execution/SanteDesComposants.cs 1
src/DrDxt.ElevatedHelper/Execution/Win32BcdActions.cs 2
src/DrDxt.ElevatedHelper/Execution/Win32FirmwareBootActions.cs 4
src/DrDxt.ElevatedHelper/Execution/Win32NetworkQosActions.cs 1
src/DrDxt.ElevatedHelper/Execution/Win32PowerPlanActions.cs 6
src/DrDxt.ElevatedHelper/Execution/Win32RegistryActions.cs 4
src/DrDxt.ElevatedHelper/Execution/Win32RestorePointActions.cs 2
src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs 4
src/DrDxt.Lanceur/Program.cs 2
src/DrDxt.ElevatedHelper/Ipc/HelperLauncher.cs 1
src/DrDxt.ElevatedHelper/Leases/HelperDiagnosticsLog.cs 1
src/DrDxt.ElevatedHelper/ModeDesinstallation.cs 1
src/DrDxt.GpuNative/AdlxNative.cs 2
src/DrDxt.GpuNative/IAdlxNative.cs 2
src/DrDxt.GpuNative/INvApiNative.cs 6
src/DrDxt.GpuNative/NvApiNative.cs 6
src/DrDxt.GpuNative/Nvml/INvmlNative.cs 2
src/DrDxt.GpuNative/Nvml/NvmlNative.cs 2
src/DrDxt.GpuNative/PawnIo/PawnIoIntel.cs 2
src/DrDxt.Infrastructure/Diagnostics/DiagnosticsLog.cs 3
src/DrDxt.Infrastructure/Updates/UpdateSeams.cs 1
src/DrDxt.Measurement/Frames/CompteurPresentsDxgi.cs 1
src/DrDxt.Reconciler/JsonReconcilerStateStore.cs 1
src/DrDxt.Reconciler/Manifest/MachineManifestKey.cs 1
src/DrDxt.Reconciler/Manifest/ProgramDataManifestStorage.cs 1
src/DrDxt.Reconciler/Manifest/TrustedManifestRoot.cs 2
src/DrDxt.Reconciler/Reseau/PlafondEnvoiDuService.cs 1
```

## H1 — MATERIEL : les 24 sites d'ecriture GPU

**C'est la seule classe irreversible, donc la seule qui compte vraiment.** Elle a ete tracee de bout
en bout le 05/09/2026, appelant par appelant.

### La chaine, telle qu'elle est ecrite

| Etage | Fichier | Ce qu'il fait |
|---|---|---|
| 1. Appelant | `GpuTuningApplyStep`, `ScanProfileConsumer`, `GpuFanCurveTweak` | **ECRETE** la valeur demandee (`GpuSafeBounds.Clamp*`) |
| 2. Executeur eleve | `NvApiTuningActions`, `ADLXTuningActions`, `NvApiDrsActions` | **REFUSE** hors bande — il n'ecrete pas, il rejette |
| 3. Primitive native | `NvApiNative`, `AdlxNative` (`TrySet*`) | ecrit, **puis RELIT** pour confirmer |

**L'etage 2 ne fait pas confiance a l'etage 1**, et le code le dit : *« the elevated write never
trusts its caller for value bounds, even on a DIRECT (non-IPC) call from a smoke tool that bypasses
OpAllowlist. Out of band is REJECTED, not clamped. »* C'est de la defense en profondeur reelle : un
outil interne qui court-circuiterait l'allowlist se heurte quand meme au rail.

### Les rails, mesures dans le code

Chaque rail a **deux faces** : l'ECRETAGE (`Clamp*`, qui change silencieusement la valeur) et le
REFUS (`Is*Safe`, qui fait rejeter le tweak). Les deux sont mutees separement ci-dessous : un rail
dont seul l'ecretage est prouve laisserait le refus mourir sans bruit.

| Grandeur | Bande | Constante | Ecretage prouve par | Refus prouve par |
|---|---|---|---|---|
| Offset d'horloge coeur | **-200 a +100 MHz** | `GpuSafeBounds.Min/MaxCoreOffsetMhz` | `ScanProfileConsumerTests.Out_of_bounds_core_offset_is_clamped_to_safe_bounds_before_reaching_the_target` (+3) | `OcSweepTests.Out_of_band_candidates_are_never_applied` (+1) |
| Offset d'horloge memoire | **-500 a +500 MHz** | `GpuSafeBounds.Min/MaxMemoryOffsetMhz` | `GpuTuningApplyStepTests.Reclamps_out_of_bounds_live_targets_to_safe_bounds_before_applying` — **un seul test** | `GpuUndervoltTweakTests.Apply_rejects_memory_offset_outside_safe_bounds` |
| Limite de puissance | **60 a 100 %** cote appli ; **50 a 100 %** cote eleve (allowlist 0-100, executeur 50-120, `NvApiTuningRailTests`) | `GpuSafeBounds.Min/MaxPowerLimitPct` | `GpuSafeBoundsTests.Power_limit_clamp_raises_below_floor_to_the_floor` (+5) | `GpuSafeBoundsTests.Is_power_limit_safe_rejects_values_below_the_floor` |
| Ventilateur au-dela de **80 °C** | **>= 40 %** de vitesse | `HotTempThresholdC`, `MinFanDutyAtHotTempPct` | `GpuSafeBoundsTests.Fan_curve_clamp_only_raises_hot_points_below_the_floor` (+2) | `GpuSafeBoundsTests.Is_fan_curve_safe_flags_a_hot_silent_curve` |

**Mesure du 06/09/2026 — 8 mutations, 8 tuees, 0 survivant.** Chaque mutation COMPILE (bande
elargie x10, plancher 60 % ramene a 1 %, remontee de duty desamorcee, borne haute retiree du
predicat), et chacune a produit au moins un echec rouge. Revert verifie : `git diff` vide.

Deux choses que la mutation a apprises et que la lecture n'aurait pas dites :

- **L'ecretage du coeur ne tombe pas dans `DrDxt.Application.Tests`.** Sa bande elargie x10 laisse
  les 390 tests du projet qui CONTIENT le code au vert ; ce sont `DrDxt.App.Tests` et
  `DrDxt.Detection.Tests` qui le tuent. Lancer le projet d'une garde seul ne dit donc rien sur elle.
- **L'ecretage memoire ne tient qu'a UN test.** C'est la maille la plus fine des quatre rails :
  supprimer `Reclamps_out_of_bounds_live_targets_to_safe_bounds_before_applying` rendrait la bande
  memoire libre sans qu'aucune autre couleur ne change.

Ces bornes existent en **DEUX exemplaires** (`GpuSafeBounds` cote application,
`NvApiTuningMath` / `AdlxTuningMath` cote executeur) — une copie derive toujours. Elles sont tenues
ensemble par `NvApiTuningPolicyBoundSyncTests` et `AdlxTuningPolicyBoundSyncTests`, qui comparent les
constantes deux a deux. **La duplication est donc assumee et gardee, pas subie.**

### Le residuel, dit et non masque

- **Aucune tension n'est jamais ecrite.** Il n'existe aucun op de tension dans le contrat privilegie
  (`NoVoltageWriteOpExistsGateTests`, `ThePrivilegedSurfaceIsClosedGateTests`). L'undervolt du
  produit est un **offset d'horloge**, pas une courbe V/F. C'est ce qui rend H1 vrai par ABSENCE.
- **UNKNOWN — l'enveloppe constructeur.** Le pilote NVIDIA borne lui-meme les offsets ; la SOURCE de
  cette enveloppe n'a pas ete retrouvee. On sait qu'on ecrit dans une bande plus etroite que celle du
  pilote, on ne sait pas la nommer. A ne pas presenter comme une garantie.
- **UNKNOWN — un plantage de pilote (TDR)** reste possible sur un offset pourtant dans la bande. Il
  est **reversible** (l'offset est volatil, il retombe a zero au redemarrage), mais le produit ne
  doit jamais ecrire « ne peut pas planter ».
- Ce que le produit a le droit de dire, exactement : **« DrDXT n'ecrit ni tension, ni firmware, ni
  VBIOS ; au GPU il n'ecrit qu'un offset d'horloge, une limite de puissance et une courbe de
  ventilateur, toutes trois bornees et relues. »**

## H2 — BIOS : ce qui peut rendre une machine non demarrable

**Le produit n'ecrit rien dans le firmware.** C'est la propriete centrale de cette classe, et elle
tient par la structure du contrat privilegie, pas par une garde qu'on pourrait desactiver.

| Site | Ce qu'il fait | Garde | Residuel | Prouve par |
|---|---|---|---|---|
| `Win32FirmwareBootActions` | `shutdown.exe /r /fw` — le **voyage** vers le setup UEFI, une facilite documentee par Microsoft | delai borne **0 a 300 s** (`OpAllowlist`), et le code de sortie de `shutdown.exe` est **LU** : un echec se dit, il ne se rattrape pas | le comportement sur un firmware inconnu est **UNKNOWN** | `OpAllowlistTests.Firmware_reboot_outside_the_band_is_denied` |
| `CancelPendingFirmwareBootOp` | `shutdown /a /fw` — **l'annulation** du precedent | — | c'est la sortie de secours du voyage | `LeaseWatchdogTests.A_firmware_reboot_can_NEVER_be_a_lease_revert` |
| `ReadFirmwareVariableOp` | **LECTURE seule** d'une variable firmware | **deux espaces de noms fermes** : sous le GUID EFI global, 3 noms (`BootOrder`, `SecureBoot`, `BootCurrent`) ; sous le GUID constructeur, le seul `Setup`. Tout autre GUID est hors de portee par construction, et un espace n'herite pas des noms de l'autre | — | `OpAllowlistTests.ReadFirmwareVariable_ForeignGuid_IsDenied` (+2) |
| `BiosMarkerFile`, `FirmwareTrip` | un marqueur **fichier** pour savoir au retour si le voyage a eu lieu | ecrit sous `ProgramData`, pas dans le firmware | H-aucune | — |

**Mesure du 06/09/2026 — 6 mutations, 6 tuees, 0 survivant.** Bornes du delai de reboot retirees
une par une (haute puis basse) ; les deux ops firmware retirees une par une de l'interdit de revert
de bail ; le GUID inconnu replie sur l'ensemble EFI au lieu d'etre hors de portee ; le nom de
variable plus verifie dans son espace de noms.

**Precision d'ecriture, corrigee le 06/09** : ce document disait « le varstore `Setup` est
REFUSE ». C'est faux au sens litteral — `Setup` est **lu** sous le GUID constructeur, et c'est le
but de l'op. Ce qui est refuse, c'est **l'ECRITURE**, et pas par une garde : par ABSENCE, puisque le
contrat privilegie ne contient aucune operation d'ecriture firmware. La formulation exacte compte,
parce que c'est elle qu'on ira relire le jour ou quelqu'un voudra ouvrir un nom de plus.

**Et la garde d'absence etait unfalsifiable jusqu'a ce jour-la.** Remplacer « setfirmware » par
« setfirmwar3 » dans sa liste de mots interdits laissait les 164 tests du projet au vert : le filtre
tourne sur une liste d'ops ou aucun nom interdit n'apparait, donc il rend « aucun coupable » que ses
motifs soient justes ou casses. Une garde d'ABSENCE ne se prouve pas sur son propre echantillon — il
faut lui fabriquer un coupable. C'est ce que fait
`ThePrivilegedSurfaceIsClosedGateTests.The_forbidden_name_filter_would_actually_catch_a_voltage_or_firmware_write`.

**Ce qui rendrait une machine non demarrable, ce n'est pas ce que le produit fait — c'est ce
qu'il DIT de faire.** Le seul chemin H2 reel passe par le **guide BIOS**, ou l'utilisateur tape
lui-meme dans son firmware. D'ou deux decisions deja prises : le **CSM est sorti du guide V1**
(conseille a l'aveugle, il empeche de demarrer), et **aucun des 9 guides n'est marque verifie** —
chacun s'affiche avec « non verifie pour ta carte », garde par deux tests.

**L'ecriture dans le varstore `Setup` reste ECARTEE** (decision de risque, pas mesure) : gain reel
de trois clics — le profil memoire s'entraine au POST, donc un redemarrage est requis de toute
facon — contre une machine potentiellement non demarrable, sans annulation.

---

## H3 — WINDOWS : deux couches, liste blanche ET liste noire

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32RegistryActions` | ecrit une valeur de registre | **liste blanche de 10 FEUILLES EXACTES** — hive + chemin + nom, **jamais un sous-arbre** | `OpAllowlistTests.Registry_allowed_path_but_wrong_value_name_is_denied_leaf_exact` (+1) |
| — | — | **plus 11 sous-arbres toujours refuses** : Windows Defender (3 chemins), Windows Update (2), `wuauserv`, BITS, `bam`, Safer, `SECURITY`, `SAM` | `OpAllowlistTests.Every_blacklisted_subtree_denies_the_key_AND_its_children_by_the_blacklist` — **ecrit le 06/09** |
| Heures d'activite de Windows Update (`fixit.heures_actives`, L-WU, 06/10/2026) | ecrit `ActiveHoursStart` et `ActiveHoursEnd` sous `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` : le reglage de Parametres, **pas la strategie** (qui reste sous liste noire). Windows Update reste allume | **deux feuilles exactes**, valeur = une heure de 0 a 23 sans zero de tete ; original relu avant, rendu par l'annulation ; propose seulement en mode manuel LU (revue F-17) | `OpAllowlistTests` (heures 0-23, valeur forgee refusee), `HeuresActivesTweakTests` (demi-ecriture rendue, F-20) |
| `Win32ServiceActions` | `sc.exe` install/start/stop/query | **deux services seulement** : `AMD3DVCacheSvc`, et le service PresentMon vendu via son op dedie | `OpAllowlistTests.Service_unknown_or_blacklisted_name_is_denied` |
| Plan d'alimentation | applique un GUID de plan | **GUID connu seulement** | `OpAllowlistTests.PowerPlan_random_guid_is_denied` |
| Mode de boost du processeur (`fixit.mode_boost`, L-CPU, 07/10/2026) | ecrit PERFBOOSTMODE sur secteur dans un plan, rien d'autre du plan | **un reglage fixe par le type**, valeur 0 a 4 documentee, plan non vide | `OpAllowlistTests.Mode_de_boost_refuse_une_valeur_forgee` (+1) |
| Reglages pilote NVIDIA (DRS) | ecrit un reglage par id | **4 ids fermes**, un scope par application n'elargit pas l'ensemble | `LatencyTweakOpsAllowlistTests.Forged_drs_setting_id_is_denied_even_with_valid_profile` (+3) |
| Suppression DRS au profil d'un jeu (`DeleteNvApiDrsSettingOp`, `NvAPI_DRS_DeleteProfileSetting` 0xE4A26362, 10/09/2026) | **retire** un reglage que le profil du jeu ne possedait pas — c'est l'INVERSE d'une ecriture au jeu, rien d'autre | **plus etroite que l'ecriture** : chemin d'executable complet uniquement, **jamais le profil de base** ; 4 ids fermes ; provenance attendue = heritee, defaut ou livree (jamais `SetByUser`, jamais `Unknown`, jamais hors enum). Le helper relit en session neuve : succes **seulement** si la provenance d'origine est revenue | `OpAllowlistTests.NvApiDrsDelete_*` (+8), `DrsPerApplicationDeleteTests` (6). 5 mutations le 10/09, 5 rouges. `[Hypothesis to test]` sur la vraie carte : « Delete sur une valeur livree la remet livree » — gate du mainteneur G2 |
| `Win32RestorePointActions` | cree un point de restauration | **PROTECTEUR** : c'est le filet, pas le risque | — |
| `HelperLauncher`, `HelperServiceLoop`, `ReconcilerHost/Worker` | lancent ou pilotent le helper | le canal reste le tube nomme ; aucun op hors des 20 | `ThePrivilegedSurfaceIsClosedGateTests` |

**Mesure du 06/09/2026 — 6 mutations, 5 tuees, 1 SURVIVANTE, et la survivante etait la plus
instructive.** Rendre `IsUnderSubtree` incapable de reconnaitre un ENFANT (la liste noire ne bannit
plus que les 11 cles exactes) laissait les 397 tests au vert. Ce n'etait pas un mutant equivalent :
un enfant de `SOFTWARE\Microsoft\Windows Defender` etait bien refuse, mais **par l'autre rideau** —
la liste blanche, qui refuse tout ce qu'elle ne connait pas. Le refus avait la meme couleur et un
motif different, et c'est precisement la difference qui compte : le jour ou une feuille sous Defender
entre dans la liste blanche, seule la liste noire s'y oppose encore.

Une garde de defense en profondeur ne se prouve donc pas par le RESULTAT — il est identique — mais
par le MOTIF du refus. Le test ecrit ce jour-la enumere le tableau reel par reflexion (plus de liste
recopiee a la main, qui en oubliait six sur onze), exige le mot « blacklist » dans le message, et
exerce la cle ET un enfant de chaque sous-arbre.

**La liste noire n'est pas redondante avec la liste blanche.** Elle repond a une faute
FUTURE : le jour ou quelqu'un ajoute une feuille par commodite, un chemin sous Defender, sous les
strategies de Windows Update ou sous son service est refuse **quoi qu'il arrive**. (07/10/2026, revue
F-14 : cette phrase disait « sous Windows Update ». Les heures d'activite du reglage utilisateur,
`WindowsUpdate\UX\Settings`, ne sont pas sous la liste noire et deux de leurs feuilles sont ecrites :
voir la ligne ci-dessus.) Une liste blanche protege du present, une liste
noire protege de la prochaine bonne idee.

---

## H-AUCUNE — les ecritures de fichiers, dites plutot qu'omises

Ces sites ecrivent des fichiers **sous `LocalAppData` ou `ProgramData`**, jamais dans
`Program Files`, jamais dans le systeme : `DpapiLicenseCache`, `LicenseAntiRollbackStore`,
`DiagnosticsLog`, `HelperDiagnosticsLog`, `JsonReconcilerStateStore`,
`ProgramDataManifestStorage`, `TrustedManifestRoot`, `UpdateSeams`, `App.xaml.cs`,
`AppServiceCollectionExtensions`, `UninstallProdSeams`, `BiosMarkerFile`, `PlafondEnvoiDuService`.
Le compte exact est celui de `tools/nonuisance/count-write-sites.py`, lu par la CI ; cette phrase ne le
recopie plus (elle disait « douze » quand la table en portait davantage).

Ils sont listes **parce qu'ils apparaissent dans le compte** : une table qui les omettrait donnerait
l'impression d'avoir trie, alors qu'elle aurait cache. Aucun ne peut abimer quoi que ce soit ; le
seul risque qu'ils portent est de **contenu** — d'ou la regle du rapport SAV : ni cle de licence, ni
nom d'utilisateur, ni chemin personnel, gardee par trois tests.

### 11/09/2026 — `CommitManifestOp`, la 20e operation : un fichier, pas un reglage

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `MachineManifestCommit` + `MachineManifestKey` (helper eleve) | cree la cle de signature PROPRE A LA MACHINE (`manifest-signing.key`, DPAPI machine), fusionne, signe et ecrit `desired-state.manifest`, derive `manifest-public.key` ; met de cote (renomme, jamais efface) un manifeste ou une cle illisibles sur reprise explicite | **l'op n'ecrit ni registre, ni pilote, ni firmware.** Sa nuisance possible est INDIRECTE : le service SYSTEM rejoue les `Tweaks` au demarrage, la desinstallation rejoue les origines elevee. D'ou `OpAllowlist.ValidateCommitManifest` : identifiants fermes (table du reconciliateur, catalogue des candidats), la valeur voulue doit se traduire en op qui PASSE l'allowlist, une origine decodable doit etre une op de RETOUR permise (jamais un redemarrage firmware, un bail, un autre commit, Defender), tailles bornees. Fichier prive : Administrateurs + SYSTEM seulement, ACL posee a la creation. Manifeste illisible jamais ecrase. | `CommitManifestOpTests` (26), `MachineManifestCommitTests` (13), `ManifesteParLeHelperTests` (7). 12 mutations le 11/09, 11 rouges ; la 12e, verte, est la defense en profondeur mesuree (l'authentification de l'appelant neutralisee dans le handshake seul : le serveur type la refait) |

Residuel, dit : ce que ces fichiers ecrivent, le service et la desinstallation l'EXECUTERONT ; la
seule barriere entre les deux est l'allowlist, qui est aussi celle de toute op executee tout de suite.
Deux sites d'ecriture de fichier de plus (`MachineManifestKey`, `MiseDeCote`), hors du compte de la
LIGNE ZERO, qui n'a pas ete recalcule ce jour-la. Le risque accepte est celui dit juste au-dessus : la barriere est l'allowlist
(voir `SECURITE.md`).

### 24/09/2026 — `ApplyUploadCapOp` / `RemoveUploadCapOp`, les 21e et 22e operations : le plafond d'envoi

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32NetworkQosActions` (helper eleve) | pose deux politiques QoS Windows en **ActiveStore** (memoire) : tout TCP+UDP plafonne a l'envoi, et une exemption pour l'exe du jeu ; relit les deux avant de rendre OK. L'inverse retire les deux et verifie qu'il n'en reste aucune. | **rien n'est persistant** : un redemarrage efface les deux politiques, donc un plafond oublie ne peut pas brider la machine au-dela de la session. `OpAllowlist.ValidateUploadCap` : nom d'exe NU par regex (le nom finit dans une commande PowerShell elevee, la regex est ce qui rend l'interpolation sure), plafond borne 1 Mbit/s..10 Gbit/s ; depuis le 28/09, un nom qui ne peut pas etre un jeu (`NomExeDeJeu.EstUnJeuPossible` : lanceur, navigateur, programme de fond, binaire de Windows) est refuse, sinon lui seul enverrait librement pendant que tout le reste est bride. `powershell.exe` resolu depuis le dossier systeme, jamais le PATH. L'inverse est inconditionnel. | `UploadCapAllowlistTests` (injections, chemins, bornes), `UploadCapOpCodecTests`, smoke natif `tools/qossmoke` (24/09 : pose, remplace sans empiler, retire, retire deux fois : PASS) |

Nuisance possible, dite : un plafond trop bas ralentit les ENVOIS des autres programmes pendant qu'il est
pose (sauvegarde cloud, stream). C'est l'effet voulu, borne a la session, et annule par l'inverse ou par
un redemarrage. Il ne touche ni aux reglages de la carte reseau, ni au registre, ni au pilote.

### 24/09/2026 — `DeleteBcdValueOp` / `SetBcdValueOp`, les 23e et 24e operations : le magasin de demarrage

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32BcdActions` (helper eleve) | `bcdedit /deletevalue {current} <element>` pour rendre la decision a Windows, ou `/set` pour reposer la valeur d'origine capturee ; RELIT `bcdedit /enum {current}` apres chaque ecriture | ensemble FERME de cinq elements (`bootstatuspolicy`, `useplatformclock`, `useplatformtick`, `disabledynamictick`, `tscsyncpolicy`) et de leurs valeurs documentees (« BCDEdit /set », Microsoft) ; entree `{current}` seulement ; `bcdedit.exe` resolu depuis le dossier systeme. Aucun de ces elements ne peut empecher le demarrage : `bootstatuspolicy` ne decide que de l'ecran de recuperation, les quatre autres de l'horloge que Windows choisit seul. `testsigning`, `nx`, `safeboot`, `device`, `path`… sont refuses, meme en retrait. | `BcdAllowlistTests` (23 cas), codec ; smoke natif du 24/09 : retrait puis repose de `bootstatuspolicy` sur la machine de reference |

Nuisance possible, dite : retirer `bootstatuspolicy` rend l'ecran de recuperation apres un demarrage rate —
c'est l'effet voulu. Retirer un element d'horloge rend a Windows le choix de sa minuterie.

### 25/09/2026 — `ReadComponentStoreHealthOp`, la 25e operation : une lecture, rien n'est ecrit

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `SanteDesComposants` (helper eleve) | `dism.exe /Online /Cleanup-Image /CheckHealth /English` et rend « sain », « reparable » ou « non reparable » (Microsoft, « Repair a Windows Image ») | op sans parametre : l'appelant ne choisit ni la commande ni ses arguments ; `/CheckHealth` seulement (jamais `/RestoreHealth` ni `/ScanHealth`) ; `dism.exe` resolu depuis le dossier systeme ; sortie non reconnue = echec, jamais un etat devine ; delai de 120 s | `SanteDesComposantsTests` (interpretation des trois phrases, sortie inconnue, codec et allowlist), mutations de l'interpretation et du codec ; lecture reelle du 25/09 sur la machine de reference : « The component store is repairable » en 0,2 s |

Nuisance possible, dite : aucune ecriture. Le cout est le lancement de DISM pendant le scan (0,2 s
mesures). La reparation (`/RestoreHealth`, `sfc /scannow`) n'est pas une op : elle reste un geste de
l'utilisateur, donne en clair dans le constat.

### 28/09/2026 : `RetainServiceUploadCapOp` / `ForgetServiceUploadCapOp`, les 26e et 27e operations : un fichier, pas un reglage

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `PlafondEnvoiDuService` (helper eleve) | ecrit ou efface `%ProgramData%\DrDxt\plafond-envoi.json` : le nom d'exe du jeu et le plafond que le service de demarrage portera. Aucun registre, aucun pilote, aucune politique reseau : c'est le service, plus tard, qui passera `ApplyUploadCapOp` par la meme allowlist. | meme borne que `ApplyUploadCapOp` (`OpAllowlist.ValidateUploadCap` : nom d'exe NU par regex, plafond 1 Mbit/s..10 Gbit/s), et depuis le 28/09 un nom qui ne peut pas etre un jeu est refuse (`NomExeDeJeu.EstUnJeuPossible` : lanceurs, navigateurs, programmes de fond, binaires de Windows), a l'ordre comme a la lecture du service ; ecriture atomique au proprietaire Administrateurs (`TrustedManifestRoot.WriteTrustedFile`) ; a la lecture, proprietaire, dossier et lien symbolique verifies, un fichier non fiable, abime ou incoherent n'allume rien. Branche dans le helper seul : le service le lit, il ne l'ecrit pas. | `PlafondEnvoiDuServiceTests` (8 cas, dont un contenu parfait dans un dossier non fiable ignore ; mutation de la verification de confiance attrapee), `PlafondRetenuPourLeServiceTests` (allowlist, passage par le magasin, echec honnete sans magasin) |

Nuisance possible, dite : aucune au moment de l'ecriture. Le fichier ne fait rien seul ; il decide seulement
de ce que le service plafonnera pendant une partie, avec la nuisance deja dite pour `ApplyUploadCapOp`
(envois des autres programmes ralentis pendant la partie, rien de persistant cote reseau).

Le service de demarrage (SYSTEM) recoit a cette date les actions QoS dans son executeur : c'est lui qui pose le plafond
pendant la partie, sans DrDXT ouvert. Il ne le demande que pour le jeu retenu, pendant qu'il tourne, par la meme
allowlist ; il retire tout a son demarrage et a son arret. Il ne recoit ni l'ecriture du reglage (branchee dans le
helper seul), ni le firmware, ni le manifeste, ni le BCD : `HostCompositionTests` le verifie sur la construction.

### 15/10/2026 — `SetStartupApprovalOp`, la 28e operation : un programme au demarrage coupe ou reactive

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32RegistryActions.SetStartupApproval` (helper eleve) | ecrit les 12 octets que le Gestionnaire des taches ecrit (02 = actif ; 03 + heure FILETIME = coupe) a la valeur du programme sous `Explorer\StartupApproved\{Run, Run32, StartupFolder}`. La commande du programme (cle Run, raccourci du dossier Demarrage) n'est jamais touchee. | liste fermee (`Run`, `Run32` en machine seulement, `StartupFolder`), ruche utilisateur ou machine, nom simple (ni `\`, ni `/`, ni caractere de controle, 260 au plus) ; `SecurityHealth` ne se coupe pas ; le programme doit etre liste la ou sa liste le dit, sinon rien n'est ecrit (un op forge ne cree pas de valeur pour un programme qui n'existe pas). | `StartupApprovalAllowlistTests` (11 cas), `SetStartupApprovalOpTests` (codec, octets), `StartupApprovalMachineTests` (une entree d'essai de la session coupee, relue, reactivee puis effacee sur la machine de reference) |

Nuisance possible, dite : un programme coupe ne se lance plus a l'ouverture de session, ce que le joueur a demande ;
il se reactive par le meme geste, dans DrDXT ou dans le Gestionnaire des taches. Le format n'est pas documente par
Microsoft : il est releve sur la machine de reference et decrit pareil par la communaute.

### 15/10/2026 — `ScheduleMemoryDiagnosticOp` / `CancelMemoryDiagnosticOp` : le test memoire au prochain demarrage

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32BcdActions` (helper eleve) | `bcdedit /bootsequence {memdiag}` : le gestionnaire de demarrage lance le Diagnostic de memoire Windows au prochain demarrage, une seule fois, puis efface la sequence (Microsoft, « BCDEdit /bootsequence », ce que fait `mdsched.exe`) ; relu par `bcdedit /enum {bootmgr}`. L'annulation retire `bootsequence` de `{bootmgr}` et relit son absence | ops sans parametre : l'entree (`{memdiag}`) et l'objet (`{bootmgr}`) sont ecrits en dur, l'appelant ne choisit rien ; refus sans ecriture si l'entree `{memdiag}` n'existe pas, ou si une autre sequence unique est en attente (jamais ecrasee) ; l'annulation ne retire que `{memdiag}` ; `bcdedit.exe` resolu depuis le dossier systeme ; jamais le revert d'un bail (`LeaseWatchdog`) ; aucun redemarrage lance par DrDXT | `TestMemoireAuDemarrageTests` (pose relue, entree absente sans ecriture, pose non relue = echec, annulation, annulation sans rien en attente), `LeaseWatchdogTests`, codec |

Nuisance possible, dite : au redemarrage suivant, le PC passe quelques minutes dans le test memoire avant Windows ;
c'est l'effet demande. La sequence ne sert qu'une fois : l'ordre de demarrage habituel n'est pas touche.

### 15/10/2026 — `RestartDeviceOp`, `RemoveDriverPackageOp` / `RestoreDriverPackageOp` : pnputil

| Site | Ce qu'il fait | Garde | Prouve par |
|---|---|---|---|
| `Win32PnpActions` (helper eleve) | `pnputil /restart-device <id>` : relance un peripherique dont le pilote est en echec. `pnputil /export-driver oemNN.inf <dossier>` puis `/delete-driver oemNN.inf` : retire un vieux paquet d'affichage du magasin de Windows apres l'avoir sauvegarde ; `/add-driver <inf exporte> /install` le remet (Microsoft Learn, « PnPUtil command syntax ») | identifiant de peripherique par regex (enumerateur, barre, puis lettres, chiffres et `& _ . - # { } \ : + ,`, sans espace ni guillemet : jamais une option) ; relance refusee si Windows ne tient pas ce peripherique pour en panne (`CM_Get_DevNode_Status`, `DN_HAS_PROBLEM`) : la carte graphique, la carte reseau ou un disque qui marchent ne se relancent pas ; nom publie `oemN.inf` seulement ; le paquet est relu dans `%windir%\INF` et doit etre de la classe Display (`[Version]`, `Class=Display` ou son GUID) ; aucune suppression sans export relu ; jamais `/force` ni `/uninstall`, donc Windows refuse un paquet encore utilise, et la relecture du magasin le dit ; l'export vit sous `%ProgramData%\DrDxt\pilotes-retires`, durci (Administrateurs et SYSTEM), relu fiable avant toute reinstallation, un dossier horodate par retrait (un `oemNN.inf` reutilise par Windows n'efface pas la sauvegarde d'avant ; la remise prend le dernier), chemin derive du nom par le helper, jamais transmis ; un seul `.inf` d'affichage par export ; `pnputil.exe` resolu depuis le dossier systeme ; jamais le revert d'un bail | `GestesPnpTests` (relance et codes, export puis retrait, paquet non Display intouche, export rate = rien retire, paquet garde par Windows, paquet absent, remise, dossier non fiable jamais installe, allowlist des identifiants et des noms, codec), `LeaseWatchdogTests` |

Nuisance possible, dite : relancer un peripherique le coupe un instant (une souris, une carte reseau). Retirer un vieux
paquet empeche Windows d'y revenir apres une mise a jour ratee, ce qui est l'effet voulu ; la sauvegarde et « Annuler »
le rendent. Le pilote en service n'est jamais propose (MagasinDePilotes), et Windows le refuserait de toute facon.

### 15/10/2026 — deux reglages DRS de plus : la surcouche DLSS d'un jeu

`ApplyNvApiDrsSettingOp` / `DeleteNvApiDrsSettingOp` admettent `NGX_DLSS_SR_OVERRIDE` (0x10E41E01, 0 ou 1) et
`NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION` (0x10E41DF3, 0 a 15 ou Latest = 0x00FFFFFF), valeurs bornees par
`ValeursDrs` d'apres NvApiDriverSettings.h. Meme portee que les autres : un chemin d'executable complet ou le profil de base,
et le produit n'ecrit qu'au profil d'un jeu qui en a deja un. Nuisance possible, dite : une image plus nette pour un calcul
un peu plus lourd ; la generation d'images (latence) n'est pas touchee. Prouve par `SurcoucheDlssTests`, `GesteDlssTests`.

## Ce que le produit a le DROIT de dire au client

Ce document sert deux lecteurs : un humain qui veut savoir ou le produit ecrit, et **l'application
elle-meme**, qui doit pouvoir dire au client ce qu'elle ne fera jamais et ce qu'elle ne sait pas.
Les deux lisent la meme source, sinon l'un des deux ment tot ou tard — et ce sera l'ecran, parce
que c'est lui qu'on oublie de relire.

Le bloc ci-dessous est la liste fermee de ces phrases. Chaque cle existe dans
`src/DrDxt.Infrastructure/Resources/Strings.resx` **et** dans `Strings.fr.resx`, et
`TheNeverAndUnknownClaimsHaveOneSourceGateTests` fait echouer la compilation du depot si les trois
listes divergent : une phrase ajoutee ici sans traduction, une phrase traduite sans figurer ici, une
phrase presente dans une seule langue.

Ce ne sont pas des slogans. **jamais** = une propriete tenue par absence ou par une garde prouvee
par mutation plus haut dans ce document. **NE SAIT pas** = un UNKNOWN assume, dit au client plutot
que masque.

```claims
NonNuisance.Never.Voltage
NonNuisance.Never.Firmware
NonNuisance.Never.Security
NonNuisance.Never.KernelDriver
NonNuisance.Never.SilentFan
NonNuisance.Unknown.VendorEnvelope
NonNuisance.Unknown.Tdr
NonNuisance.Unknown.UnknownFirmware
NonNuisance.Unknown.NoBootRecovery
NonNuisance.Unknown.WindowsFeatureUpdate
```

Retirer une cle d'ici est un geste produit, pas un nettoyage : c'est promettre moins, ou avouer
moins. Le test le rend visible, il ne l'empeche pas.

## Ce qui reste a prouver

- **N3 — le contrefactuel sur la vraie machine** (appliquer a la borne extreme, relire, revert,
  relire) demande une session **elevee**. Non fait le 05/09.
- **N4 — H2 et H3 reels** (no-boot puis recuperation ; survie a une mise a jour de
  fonctionnalite Windows) demandent une **machine sacrifiable ou une VM**. Non fait le 05/09.
- Les gardes listees ici sont **prouvees par mutation** une par une dans le lot LD-N2. Faites le
  06/09 : les **4 rails de `GpuSafeBounds`** (H1, 8 mutations) et les **cinq refus d'`OpAllowlist`**
  (H3, 6 mutations, dont la survivante qui a produit un test) et les **quatre gardes firmware**
  (H2, 6 mutations, plus la garde d'absence rendue falsifiable). **Vingt mutations, deux
  survivantes, deux tests ecrits en reponse.**
