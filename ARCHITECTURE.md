# Architecture

How DrDXT is put together, the rules the code keeps, and how to add a setting from end to end. For building, testing
and proposing a change, see [CONTRIBUTING.md](CONTRIBUTING.md).

## The big picture

DrDXT runs as three programs:

- **The app** (`src/DrDxt.App`, WPF) reads the machine, explains it and asks before changing anything. It never writes
  a Windows or hardware setting itself.
- **The helper** (`src/DrDxt.ElevatedHelper`) is the only elevated program that writes. It receives an operation,
  checks it against its allowlist (default deny, exact targets), reads the original value, writes, and keeps the
  inverse. The native launcher (`src/DrDxt.Lanceur`) receives the elevation and starts it with a clean environment.
- **The service** (`src/DrDxt.ReconcilerHost`, `DrDxtReconciler`, optional) reapplies at Windows startup the settings
  you kept, from a signed manifest, and the uninstaller reads that same manifest to revert them.

```
            App (WPF)  ─── asks ───>  Helper (elevated, allowlist)  ───>  registry, driver, power plan
               │                          │
               │ reads                    └── writes the signed manifest
               v                                       │
   Detection, Measurement, Bios                        v
                                        Service (startup)  ─── reapplies through the helper
```

## Projects and dependencies

`DrDxt.Core` depends on nothing and everything depends on it. Arrows point to what a project references.

| Project | Role | References |
|---|---|---|
| `DrDxt.Core` | contracts, `Result`, error codes, privileged operations, the doctor's sentences | nothing |
| `DrDxt.GpuNative` | NVAPI, ADLX and NVML bindings, from the public headers | nothing |
| `DrDxt.Lanceur` | native launcher that receives elevation | nothing |
| `DrDxt.Measurement` | PresentMon, DXGI fallback, network, memory test | Core |
| `DrDxt.Application` | the settings catalog, apply engine, rollback, autopilot, GPU bounds | Core, Measurement |
| `DrDxt.Detection` | hardware, settings state, network, the health audit | Core, Application, GpuNative |
| `DrDxt.Bios` | BIOS reading and the step-by-step guide; never a write | Core |
| `DrDxt.Profiles` | per-chip profiles | Core |
| `DrDxt.Infrastructure` | the app's texts (`Strings.resx`) and localizer | Core |
| `DrDxt.Reconciler` | signed manifest of kept settings | Core |
| `DrDxt.Reconciler.Adapters` | bridges the reconciler to detection | Core, Reconciler, Detection |
| `DrDxt.ElevatedHelper` | the writer, behind its allowlist | Core, GpuNative, Reconciler |
| `DrDxt.ReconcilerHost` | the Windows service | Core, Reconciler, Adapters, Detection, Infrastructure, ElevatedHelper |
| `DrDxt.App` | the user interface | almost everything above |

One reference goes the unexpected way: `DrDxt.Detection` depends on `DrDxt.Application`. It is known debt, not a
pattern to follow; new detection code should depend on Core only.

Benches and probes that run outside the solution live under `tools/`. The CI gates are in `tools/gates`.

## The rules the code keeps

Each comes from a real failure, and a test holds it.

- **Do no harm.** DrDXT writes only what its helper allowlist names: never Windows Defender or Windows Update, never
  firmware, and the only services it touches are AMD's 3D V-Cache service and its own PresentMon service. The write sites are listed in [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md), and CI recounts them.
- **Not read is not healthy.** A value DrDXT could not read is reported as "not read", never as "fine". Every lever
  says so through `EtatNonLu`, and the result screen counts what was not read next to what was.
- **A gain counts only above the noise.** A setting is kept only if the measured difference is larger than the noise
  of the measurement. "No measurable gain" is a valid answer. What each instrument can prove is in
  [docs/INSTRUMENTS.md](docs/INSTRUMENTS.md) and [docs/OU-VIT-LE-GAIN.md](docs/OU-VIT-LE-GAIN.md).
- **Every write has its origin and its way back.** The original value is read before writing and stored with the
  setting; reverting restores exactly that value. A lever that writes without a revert fails
  `tests/DrDxt.Application.Tests/ChaqueLevierQuiEcritSaitDefaireTests.cs`.
- **No myths.** Settings with no proven effect (Nagle, `NetworkThrottlingIndex`, 8,000 Hz polling, realtime priority,
  C-states disabled wholesale) are refused by
  `tests/DrDxt.Application.Tests/Tweaks/LaBarriereAntiMytheRegardeLeVraiCatalogueTests.cs`.
- **No score.** The app says "N to fix out of M read, K not read": three counts, all facts of reading.
  `tests/DrDxt.App.Tests/AucunTexteNePrometUnScoreTests.cs` keeps a score from coming back.

## Adding a setting in five steps

A setting is a lever: a class that knows when it applies, how to apply itself and how to revert. Game Mode
(`src/DrDxt.Application/Tweaks/Latency/GameModeOnTweak.cs`) is a short, complete example to copy.

1. **The lever.** Implement `ITweak` (`src/DrDxt.Core/Application/TweakContracts.cs`), usually through a family base
   class such as `src/DrDxt.Application/Tweaks/Latency/LatencyTweakBase.cs`. Give it an `Id`, `AppliesTo` (when the
   setting is wrong on this machine), `EtatNonLu` (when it could not be read), and `ApplyAsync` that reads the original
   before writing and returns it in the snapshot. Register it in
   `src/DrDxt.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs`.
2. **The helper allowlist.** Add the exact target (hive, key, value name, type) to
   `src/DrDxt.ElevatedHelper/OpAllowlist.cs`. Anything not listed is refused, and
   `tests/DrDxt.ElevatedHelper.Tests/OpAllowlistTests.cs` shows how to test the new entry.
3. **The test that reverts it.** Write the lever's tests first, next to the others under
   `tests/DrDxt.Application.Tests/Tweaks`. The bench in
   `tests/DrDxt.Integration.Tests/ChaqueLevierDuProduitSaitSeDefaireTests.cs` builds every registered lever, applies it,
   reverts it and requires the machine to be identical afterwards; it fails if your lever is registered but unknown
   to the bench.
4. **The text.** Map the lever's title and summary keys in `src/DrDxt.App/ViewModels/TweakDisplayKeys.cs`, then write
   both sentences in `src/DrDxt.Infrastructure/Resources/Strings.resx` (English) and `Strings.fr.resx` (French).
   `tests/DrDxt.App.Tests/EveryCandidateHasATitleTests.cs` refuses a lever without a title in either language.
5. **The catalog and the coverage.** Describe the lever in `src/DrDxt.Core/Autopilot/CatalogueDeCandidats.cs` (when
   it is tried, and whether it takes effect after a restart), and give it its domain in
   `src/DrDxt.Application/Tweaks/CouvertureDesLeviers.cs`. Then regenerate [docs/COUVERTURE.md](docs/COUVERTURE.md) by
   running `tests/DrDxt.App.Tests/CouvertureDocTests.cs` with `DRDXT_COUVERTURE=ecrire`.
   `tests/DrDxt.App.Tests/CatalogueDeCandidatsTests.cs` fails if a registered lever is missing from the catalog.

If the setting writes a new registry value, name it in the "What DrDXT writes on your machine" section of both READMEs.
`tests/DrDxt.App.Tests/LeReadmeDitCeQueLeProduitEcritTests.cs` compares the French one with the helper allowlist.

## Where the texts live

- **The app's interface**: `src/DrDxt.Infrastructure/Resources/Strings.resx` and `Strings.fr.resx`, read through
  `ILocalizer` (`src/DrDxt.Core/Localization/LocalizationContracts.cs`).
- **The doctor's sentences** (findings, explanations, reports): `src/DrDxt.Core/Resources/Docteur.resx` and
  `Docteur.fr.resx`, read through `Docteur.T` (`src/DrDxt.Core/Localization/Docteur.cs`). Both files keep the same keys,
  checked by `tests/DrDxt.Core.Tests/DocteurParleLaLangueDeLAppTests.cs`.

English is the default file, French the `.fr` one. The app follows the Windows display language. Texts never carry an
em dash, shouting capitals or an emoji, and address the player as "you" ("tu" in French); tests read the resx files
for that.

## Running the tests and the gates

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

Tests marked `Machine` read the real hardware and run by hand. CI also runs the Python gates in `tools/gates`, each with
a `--autotest` mode:

- `tools/gates/liens.py`: every path and link cited by the READMEs, CONTRIBUTING and this file exists.
- `tools/gates/compte_des_tests.py`: the test count in the README is the last run's.
- `tools/gates/hors_ci.py`: the inventory of tests that do not run in CI is exact.
- `tools/gates/ages.py`: the dates in `GATES.md` are readable.

## Why things are the way they are

Decisions that look odd from the outside, with where the detail lives.

- **A separate elevated helper.** The app runs as the player; only the helper is elevated, and it trusts nothing it
  receives: each operation is checked by type and exact target. Threat model and accepted limits:
  [docs/SECURITE.md](docs/SECURITE.md).
- **The BIOS is read, never written.** There is no clean, universal interface to change settings on every
  motherboard without a per-BIOS mapping, and a wrong write can leave a PC that does not boot. DrDXT finds what is
  wrong, gives the exact BIOS path, guides the player on their phone and checks the result back in Windows.
- **Graphics card tuning is power limit, clock offset and fan speed, never voltage.** All are bounded, and the Expert
  mode values are reverted on their own if DrDXT stops.
- **The settings catalog ships with the code.** There is no remote channel that could change what DrDXT writes
  without a release, reviewed and tested like the rest.
- **DrDXT ships its own PresentMon service.** Another PresentMon service (Intel's, for one) can already be running;
  how both coexist without asking the player to stop anything:
  [docs/PRESENTMON-COEXISTENCE.md](docs/PRESENTMON-COEXISTENCE.md).
- **One anonymizer.** Everything that leaves the machine through a report goes through
  `MachineExportee.AnonymiserTexte` (`src/DrDxt.Detection/Repair/MachineExportee.cs`).

## Language of the code

The code is written in French, on purpose: identifiers, comments and test names, so that the project's owner can
read every line and keep improving it. New code follows the same rule. Only what the player reads is in English too
(`Strings.resx`, `Docteur.resx`, the READMEs, CONTRIBUTING and this file), along with names imposed by a framework or
an API.

Each file and each non-trivial class opens with a short French summary (`/// <summary>`, two to four lines): what the
piece does, for whom, and why it exists, in plain words. A hard function gets one line above it with the idea. A
comment says why the code is the way it is; the history of a change lives in git. A past failure stays only when it
keeps someone from repeating it, written as a fact: what happens, the proof, what it requires from the code.

Issues and pull requests are welcome in English or French.
