# Ajouter un réglage sans danger

English version: `docs/AJOUTER-UN-REGLAGE.md`.

Un réglage (un levier) écrit sur le PC de quelqu'un d'autre. `ARCHITECTURE.md`, « Adding a setting in five steps »,
dit comment en construire un. Cette page est la liste qu'un levier doit passer avant d'être livré. Chaque point nomme
le fichier qui tient la règle et le test qui tombe quand on la casse. Un point qu'on ne peut pas cocher est une raison
de s'arrêter et de demander, pas de contourner le test.

## 1. Le test rouge d'abord

Écris les tests du levier avant son code, à côté des autres dans `tests/DrDxt.Application.Tests/Tweaks`, au seam
public (`ITweak` dans `src/DrDxt.Core/Application/TweakContracts.cs` : `AppliesTo`, `EtatNonLu`, `ApplyAsync`,
`RevertAsync`). Vois-le échouer, puis fais-le passer. Un test qui ne peut pas échouer ne prouve rien.

## 2. La liste blanche du helper reste fermée, avec ses bornes

Chaque écriture passe par le helper élevé, qui refuse tout ce qu'il ne liste pas exactement : ruche, clé, nom de
valeur, type, et les valeurs ou la plage admises.

- Règle : `src/DrDxt.ElevatedHelper/OpAllowlist.cs`. Ajoute la feuille exacte et ses bornes, jamais un joker.
- Test : `tests/DrDxt.ElevatedHelper.Tests/OpAllowlistTests.cs` (la nouvelle entrée, et une valeur juste hors des
  bornes, refusée).
- Un nouveau type d'opération fait aussi tomber `tests/DrDxt.Core.Tests/ThePrivilegedSurfaceIsClosedGateTests.cs`
  tant qu'il n'y est pas nommé exprès.
- Chaque écriture de chaque levier, posé puis rendu, doit passer la liste blanche :
  `Chaque_ecriture_d_un_levier_pose_et_rendu_passe_l_allowlist_du_helper` dans
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.

## 3. L'inverse exact, lu avant d'écrire, relu

`ApplyAsync` lit la valeur d'origine avant d'écrire et la rend dans le snapshot ; `RevertAsync` réécrit exactement
cette valeur. Une origine absente se rend absente (une suppression), pas par une valeur par défaut.

- Règle : un levier qui écrit sans retour fait tomber `tests/DrDxt.Application.Tests/ChaqueLevierQuiEcritSaitDefaireTests.cs`.
- Le banc `tests/DrDxt.Integration.Tests/ChaqueLevierDuProduitSaitSeDefaireTests.cs` pose puis annule chaque levier
  et exige une machine identique (`Applique_puis_annule_rend_la_machine_identique`), et exige qu'un levier change
  vraiment quelque chose (`Un_levier_qui_n_ecrit_rien_ne_prouve_rien_et_doit_le_declarer`).
- Rien avant : `Sur_une_origine_absente_appliquer_puis_annuler_ne_laisse_rien` dans
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.
- Relecture : la vérification de la machine et l'autopilote relisent la valeur par `AppliedStateVerifier`, dans
  `src/DrDxt.Application/Tweaks/AppliedStateEvidence.cs`. Un genre d'écriture qu'il ne connaît pas se lit « non vu »,
  et le levier ne peut alors jamais être confirmé : apprends-lui le nouveau genre, avec un test.

## 4. La classe du journal d'écriture anticipée

Avant d'exécuter une écriture, le helper note son image « avant », pour qu'un arrêt en plein milieu se défasse. Une
valeur de la session (HKCU), que l'App écrit elle-même, suit la même règle dans le journal du compte
(`JournalDEcritureAnticipee.DuCompte`), que l'App rejoue à son lancement (`RejeuDuJournalDuCompte`).

- Règle : chaque type d'opération a sa classe dans `src/DrDxt.ElevatedHelper/Journal/ClassesDuJournal.cs`
  (journalisée, lecteur à venir, volatile, ou sans état).
- Tests : `tests/DrDxt.ElevatedHelper.Tests/ClassesDuJournalTests.cs`. Une opération nouvelle sans classe fait tomber
  `Chaque_op_privilegiee_a_une_classe_du_journal` ; une journalisée doit avoir une lecture de sa cible
  (`Une_op_journalisee_a_une_lecture_de_sa_cible`, lecteurs dans `src/DrDxt.ElevatedHelper/Execution/LectureDesCiblesWin32.cs`).

## 5. La portée de pose

Les chemins de pose ouvrent une `PorteeDePose` (`src/DrDxt.Core/Elevation/PorteeDePose.cs`) autour du seul
`ApplyAsync` : dedans, les écritures journalisées partent marquées, pour que le helper rende l'origine si l'App meurt
avant de l'avoir notée. Une annulation ne doit jamais partir marquée, sinon le helper la rejouerait et remettrait le
levier.

- Test : `Une_annulation_dans_une_portee_de_pose_part_hors_portee` dans `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.

## 6. Rendu après un plantage

Si DrDXT est tué pendant un essai, deux mécanismes distincts rendent l'origine. Les deux listes sont fermées : un
levier qui n'y est pas n'est jamais rendu.

- **Le rejeu du helper**, quand l'App est morte après l'écriture mais avant d'avoir noté l'origine : le helper rejoue
  son journal d'écriture anticipée (point 4). `RevertAuDemarrage.LeviersJournalises`, dans
  `src/DrDxt.Application/Autopilot/RevertEnAttente.cs`, nomme les leviers que ce rejeu rend vraiment ; le message de
  démarrage de l'App s'appuie dessus. Test : `Tue_entre_l_ecriture_et_l_origine_gardee_le_rejeu_du_helper_rend_l_origine`
  dans `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` tombe si la liste et le rejeu ne disent pas la
  même chose.
- **Le démarrage suivant de l'App**, quand l'origine a été notée : l'App la réécrit, mais seulement pour les types
  d'inverse que chaque levier est connu pour écrire, `InverseParLevier` et `FeuillesParLevier` dans le même fichier.
  Test : `Tue_apres_l_origine_gardee_le_demarrage_suivant_rend_l_origine` dans le même fichier de tests.

## 7. La désinstallation

Désinstaller DrDXT rend ce qu'il a gardé, et seulement là où la valeur courante est encore celle que DrDXT a posée.

- Règle : ce qui entre au manifeste de désinstallation, `ToRevertOnly` dans `src/DrDxt.App/AppliedTweakManifestMapper.cs` ;
  ce qui est réécrit, `src/DrDxt.Reconciler/Desinstallation/RenduALaDesinstallation.cs` (une valeur que DrDXT ne sait
  pas comparer n'est pas touchée).
- Tests : `Le_rendu_de_desinstallation_ne_rend_que_l_origine_quel_que_soit_le_chemin_d_inscription` dans
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` (sa liste `RestentAuJoueur` nomme les réglages que la
  désinstallation laisse exprès : en ajouter un est une décision), `tests/DrDxt.ElevatedHelper.Tests/RenduALaDesinstallationTests.cs`
  et `tests/DrDxt.App.Tests/LeviersDeSessionRendusALaDesinstallationTests.cs`.

## 8. Le banc des invariants couvre le nouveau levier

Les invariants ci-dessus tournent pour chaque levier que le banc construit. Ajoute le nouveau levier à
`TousLesLeviers` dans `tests/DrDxt.Integration.Tests/ChaqueLevierDuProduitSaitSeDefaireTests.cs` :
`Le_banc_construit_TOUS_les_leviers_que_le_produit_enregistre` tombe si un levier enregistré y manque, et
`tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` reprend la même liste.

## 9. La non-nuisance et les sites d'écriture

`docs/NON-NUISANCE.md` dit où le produit écrit sur la machine du joueur et quel test prouve chaque garde.

- `tools/nonuisance/count-write-sites.py` recalcule les sites d'écriture et fait tomber la CI si un site apparaît,
  disparaît ou change de compte : mets la table à jour dans le même changement.
- `tools/nonuisance/check-guard-tests.py` vérifie que chaque garde nommée dans la table a un vrai test.

## 10. Ce que la doc promet

- Une nouvelle valeur de registre est nommée dans la section « Ce que DrDXT écrit sur ta machine » des deux README :
  `tests/DrDxt.App.Tests/LeReadmeDitCeQueLeProduitEcritTests.cs`.
- Un titre et un résumé dans les deux langues : `tests/DrDxt.App.Tests/EveryCandidateHasATitleTests.cs`.
- Aucun réglage sans effet prouvé : `tests/DrDxt.Application.Tests/Tweaks/LaBarriereAntiMytheRegardeLeVraiCatalogueTests.cs`.
- Aucun gain affiché sans sa source, et `docs/COUVERTURE.md` régénéré (voir ARCHITECTURE, étape 5).

## 11. La relecture

Un changement qui écrit sur la machine n'est fusionné qu'après la relecture d'une deuxième personne, et avec la CI
verte (voir `CONTRIBUTING.fr.md`).
