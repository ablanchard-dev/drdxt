# Adding a setting safely

Version française : `docs/AJOUTER-UN-REGLAGE.fr.md`.

A setting (a lever) writes on someone else's PC. `ARCHITECTURE.md`, "Adding a setting in five
steps", says how to build one. This page is the checklist a lever must pass before it ships. Every point names the
file that holds the rule and the test that fails when the rule is broken. A point you cannot tick is a reason to stop
and ask, not to work around the test.

## 1. The failing test comes first

Write the lever's tests before its code, next to the others in `tests/DrDxt.Application.Tests/Tweaks`, at the public
seam (`ITweak` in `src/DrDxt.Core/Application/TweakContracts.cs`: `AppliesTo`, `EtatNonLu`, `ApplyAsync`,
`RevertAsync`). See it fail, then make it pass. A test that cannot fail proves nothing.

## 2. The helper allowlist stays closed, with bounds

Every write goes through the elevated helper, which refuses anything it does not list exactly: hive, key, value
name, type, and the allowed values or range.

- Rule: `src/DrDxt.ElevatedHelper/OpAllowlist.cs`. Add the exact leaf and its bounds, never a wildcard.
- Test: `tests/DrDxt.ElevatedHelper.Tests/OpAllowlistTests.cs` (the new entry, and a value just outside the bounds,
  refused).
- A new operation type also fails `tests/DrDxt.Core.Tests/ThePrivilegedSurfaceIsClosedGateTests.cs` until it is
  named there on purpose.
- Every write of every lever, applied and reverted, must pass the allowlist:
  `Chaque_ecriture_d_un_levier_pose_et_rendu_passe_l_allowlist_du_helper` in
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.

## 3. The exact inverse, read before writing, read back

`ApplyAsync` reads the original value before writing and returns it in the snapshot; `RevertAsync` writes back exactly
that value. An absent original is put back as absent (a deletion), not as a default.

- Rule: a lever that writes without a way back fails `tests/DrDxt.Application.Tests/ChaqueLevierQuiEcritSaitDefaireTests.cs`.
- The bench `tests/DrDxt.Integration.Tests/ChaqueLevierDuProduitSaitSeDefaireTests.cs` applies then reverts every
  lever and requires the machine to be identical (`Applique_puis_annule_rend_la_machine_identique`), and requires a
  lever to actually change something (`Un_levier_qui_n_ecrit_rien_ne_prouve_rien_et_doit_le_declarer`).
- With nothing there before: `Sur_une_origine_absente_appliquer_puis_annuler_ne_laisse_rien` in
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.
- Readback: the machine check and the autopilot read the value back through `AppliedStateVerifier` in
  `src/DrDxt.Application/Tweaks/AppliedStateEvidence.cs`. A kind of write it does not know reads as "not seen", and the
  lever can then never be confirmed: teach it the new kind, with a test.

## 4. The write-ahead journal class

Before executing a write, the helper notes its "before" image, so that a crash halfway can be undone. A session value
(HKCU), which the App writes itself, follows the same rule in the account journal (`JournalDEcritureAnticipee.DuCompte`),
which the App replays when it starts (`RejeuDuJournalDuCompte`).

- Rule: every operation type has a class in `src/DrDxt.ElevatedHelper/Journal/ClassesDuJournal.cs` (journaled,
  reader to come, volatile, or without state).
- Tests: `tests/DrDxt.ElevatedHelper.Tests/ClassesDuJournalTests.cs`. A new operation without a class fails
  `Chaque_op_privilegiee_a_une_classe_du_journal`; a journaled one must have a reader for its target
  (`Une_op_journalisee_a_une_lecture_de_sa_cible`, readers in `src/DrDxt.ElevatedHelper/Execution/LectureDesCiblesWin32.cs`).

## 5. The pose scope

The apply paths open a `PorteeDePose` (`src/DrDxt.Core/Elevation/PorteeDePose.cs`) around `ApplyAsync` only: inside
it, journaled writes are marked so the helper can put the origin back if the app dies before saving it. A revert must
never be marked, or the helper would replay it and put the lever back.

- Test: `Une_annulation_dans_une_portee_de_pose_part_hors_portee` in `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs`.

## 6. Put back after a crash

If DrDXT is killed during a trial, two separate mechanisms put the origin back. Both lists are closed: a lever that is
not in them is never put back.

- **The helper's replay**, when the app died after writing but before saving the origin: the helper replays its
  write-ahead journal (point 4). `RevertAuDemarrage.LeviersJournalises` in
  `src/DrDxt.Application/Autopilot/RevertEnAttente.cs` names the levers this replay really puts back; the app's start
  message relies on it. Test: `Tue_entre_l_ecriture_et_l_origine_gardee_le_rejeu_du_helper_rend_l_origine` in
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` fails if the list and the replay disagree.
- **The app's next start**, when the origin was saved: the app writes it back, but only for the inverse types each
  lever is known to write, `InverseParLevier` and `FeuillesParLevier` in the same file. Test:
  `Tue_apres_l_origine_gardee_le_demarrage_suivant_rend_l_origine` in the same test file.

## 7. Uninstall

Uninstalling DrDXT puts back what it kept, and only where the current value is still the one DrDXT set.

- Rule: what enters the uninstall manifest, `ToRevertOnly` in `src/DrDxt.App/AppliedTweakManifestMapper.cs`; what is
  written back, `src/DrDxt.Reconciler/Desinstallation/RenduALaDesinstallation.cs` (a value DrDXT cannot compare is
  left alone).
- Tests: `Le_rendu_de_desinstallation_ne_rend_que_l_origine_quel_que_soit_le_chemin_d_inscription` in
  `tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` (its `RestentAuJoueur` list is the settings
  uninstall deliberately leaves: adding one is a decision), `tests/DrDxt.ElevatedHelper.Tests/RenduALaDesinstallationTests.cs`
  and `tests/DrDxt.App.Tests/LeviersDeSessionRendusALaDesinstallationTests.cs`.

## 8. The invariant bench covers the new lever

The invariants above run for every lever the bench builds. Add the new lever to `TousLesLeviers` in
`tests/DrDxt.Integration.Tests/ChaqueLevierDuProduitSaitSeDefaireTests.cs`:
`Le_banc_construit_TOUS_les_leviers_que_le_produit_enregistre` fails if a registered lever is missing, and
`tests/DrDxt.Integration.Tests/InvariantsDesLeviersTests.cs` reuses the same list.

## 9. Non-nuisance and the write sites

`docs/NON-NUISANCE.md` says where the product writes on the player's machine and which test proves each guard.

- `tools/nonuisance/count-write-sites.py` recomputes the write sites and fails in CI if one appears, disappears or
  changes count: update the table in the same change.
- `tools/nonuisance/check-guard-tests.py` checks that every guard named in the table has a real test.

## 10. What the docs promise

- A new registry value is named in the "What DrDXT writes on your machine" section of both READMEs:
  `tests/DrDxt.App.Tests/LeReadmeDitCeQueLeProduitEcritTests.cs`.
- A title and a summary in both languages: `tests/DrDxt.App.Tests/EveryCandidateHasATitleTests.cs`.
- No setting without a proven effect: `tests/DrDxt.Application.Tests/Tweaks/LaBarriereAntiMytheRegardeLeVraiCatalogueTests.cs`.
- No gain shown without its source, and `docs/COUVERTURE.md` regenerated (see ARCHITECTURE, step 5).

## 11. Review

A change that writes on the machine is merged only after a review by a second person and with CI green (see
`CONTRIBUTING.md`).
