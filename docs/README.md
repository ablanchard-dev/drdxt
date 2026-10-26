# DrDXT documentation

What each file in `docs/` is for. Several were written in French and are read by tests, which is why they keep their
names; the summaries below say what they contain, and the ones a tester or reviewer needs exist in English.

## For testers

| File | What it is |
|---|---|
| [VALIDATION-AMD.md](VALIDATION-AMD.md) ([fr](VALIDATION-AMD.fr.md)) | What to check on an AMD Radeon or Ryzen PC, path by path, and what the report must show. |
| [TESTEUR-EXPORTER-MA-MACHINE.en.md](TESTEUR-EXPORTER-MA-MACHINE.en.md) ([fr](TESTEUR-EXPORTER-MA-MACHINE.md)) | How to export what DrDXT reads on your PC, what the file contains and what is removed from it before it leaves. |
| [COUVERTURE.md](COUVERTURE.md) (fr, generated) | Setting by setting, what DrDXT does on each kind of PC (desktop Intel + NVIDIA, Ryzen X3D + AMD, integrated graphics, NVIDIA laptop, handheld), and the reason where it does nothing. Generated from the code by `CouvertureDocTests`. |
| [guide-bios/](guide-bios/index.html) | The step-by-step BIOS guide the app opens on your phone through a QR code. |

## For contributors

| File | What it is |
|---|---|
| [AJOUTER-UN-REGLAGE.md](AJOUTER-UN-REGLAGE.md) ([fr](AJOUTER-UN-REGLAGE.fr.md)) | The safety checklist a new setting must pass before it ships, each point with the test that holds it. |
| [NON-NUISANCE.md](NON-NUISANCE.md) (fr) | Every place DrDXT can write on a machine (GPU, BIOS, Windows, files), grouped by what could go wrong. CI recounts the write sites against it (`tools/nonuisance/count-write-sites.py`). |
| [INSTRUMENTS.md](INSTRUMENTS.md) (fr) | The measuring instruments, the noise floor each one showed when it was run, and the rule that a difference smaller than that floor is not a result. |
| [PROTOCOLES-DE-MESURE-IN-GAME.md](PROTOCOLES-DE-MESURE-IN-GAME.md) (fr) | How a real match is cut into passes so a 1 to 3 % gain stays measurable, the data behind the 15 s pass, and the methods tried and rejected. |
| [MISE-A-JOUR.md](MISE-A-JOUR.md) (fr) | How the app updates itself: an Ed25519-signed manifest, the SHA-256 of the installer checked before it runs, never installed without the player. |
| [PRESENTMON-COEXISTENCE.md](PRESENTMON-COEXISTENCE.md) (fr) | How DrDXT's PresentMon service lives next to another one (Intel's) without asking the player to stop anything. |

## Security

| File | What it is |
|---|---|
| [SECURITE.en.md](SECURITE.en.md) ([fr](SECURITE.md)) | The threat model: the triage of vulnerability advisories, the asset-by-attacker table with the test that holds each line, and the limits accepted after each review. |
| `vuln-*.txt` | The raw output of `dotnet list package --vulnerable --include-transitive` at each triage, kept as evidence; `TheVulnerabilityTriageIsCompleteGateTests` checks the triage against it. |
| [RAPPORT-VERIFICATION.en.md](RAPPORT-VERIFICATION.en.md) ([fr](RAPPORT-VERIFICATION.md)) | Each claim the project makes, the test that proves it, and what is still unknown. A script refuses a test name that does not resolve. |

## Where the gain comes from

These pages are the source of what the app is allowed to claim. Each line carries a source and the date it was read,
or the exact word `UNKNOWN`; a test refuses a figure without one.

| File | What it is |
|---|---|
| [A-FAIRE-A-LA-MAIN.md](A-FAIRE-A-LA-MAIN.md) (fr) | What moves latency, smoothness, FPS and network lag on a Windows PC, and for each: whether DrDXT sets it, only reads it and tells you, or leaves it to you (BIOS, hardware, in-game settings), with the steps. The app links to it. |
| [OU-VIT-LE-GAIN.md](OU-VIT-LE-GAIN.md) (fr) | Why there is not always a gain: a gain only exists where a fault does, so the product promises a proven verdict, not a number. |
| [RAM-ETAT-DE-LART.md](RAM-ETAT-DE-LART.md) (fr) | What is established about memory (XMP/EXPO, timings, stability) and what is not, including the myths. |
| [LEVIERS-REPOS-ET-RESEAU.md](LEVIERS-REPOS-ET-RESEAU.md) (fr) | Idle power with several screens at different refresh rates, and network card power saving: what the sources say. |
